using ArturRios.Heimdall.WebApi.Tests.Support;
using ArturRios.Util.Test.Attributes;
using Npgsql;

namespace ArturRios.Heimdall.WebApi.Tests;

/// <summary>
///     Verifies that the migration applied to the throwaway container produces the schema the design
///     calls for: everything under <c>heimdall</c>, with singular snake_case table names.
/// </summary>
[Collection(nameof(FunctionalCollection))]
public class SchemaTests(PostgresFixture fixture)
{
    [FunctionalFact]
    public async Task GivenMigrationsApplied_WhenSchemaInspected_ThenTablesAreSingularSnakeCase()
    {
        string[] expected =
        [
            "application",
            "audit_log",
            "data_protection_keys",
            "email_verification_token",
            "google_user",
            "password_reset_token",
            "person",
            "role",
            "scope",
            "scope_owner",
            "scope_permission",
            "scope_user",
            "two_factor_auth",
            "two_factor_email_code",
            "two_factor_recovery_code"
        ];

        var actual = await ReadTableNamesAsync();

        Assert.Equal(expected, actual);
    }

    [FunctionalFact]
    public async Task GivenTwoUsersOfOneScopeSharingAnAddress_WhenInserted_ThenTheDatabaseRefusesTheSecond()
    {
        // The concurrency test in PersonControllerCreateUserTests drives the real endpoint, but it
        // cannot fail on demand: whether four requests genuinely interleave is up to the scheduler,
        // so it would go green against an unprotected database on any run where they happened not
        // to. This one asks the question directly — insert the duplicate the race would have
        // produced, with no handler involved, and require the database itself to refuse it.
        var scopeId = await InsertScopeAsync();

        await InsertUserAsync(scopeId, "duplicate@test.local");

        // Cased differently on purpose: the index is over LOWER(email), matching how the handlers
        // compare addresses. Over the raw column these would be two free addresses.
        var second = await Record.ExceptionAsync(() => InsertUserAsync(scopeId, "DUPLICATE@test.local"));

        var failure = Assert.IsType<PostgresException>(second);
        Assert.Equal("23505", failure.SqlState);
        Assert.Equal("ux_person_scope_user_email", failure.ConstraintName);
    }

    [FunctionalFact]
    public async Task GivenTheFirstUserIsLogicallyDeleted_WhenTheAddressIsReused_ThenTheDatabaseAllowsIt()
    {
        // The index is partial on is_deleted = false, matching the handlers' check. A logically
        // deleted User releases their address, and the database has to agree — an index that kept it
        // reserved would refuse a create the application had already accepted.
        var scopeId = await InsertScopeAsync();

        await InsertUserAsync(scopeId, "released@test.local", isDeleted: true);

        var reuse = await Record.ExceptionAsync(() => InsertUserAsync(scopeId, "released@test.local"));

        Assert.Null(reuse);
    }

    [FunctionalFact]
    public async Task GivenTwoScopes_WhenBothHaveAUserOnOneAddress_ThenTheDatabaseAllowsIt()
    {
        // Uniqueness is per scope, not global: the scope is the tenancy boundary, and the same
        // address in two scopes is two different people.
        var first = await InsertScopeAsync();
        var second = await InsertScopeAsync();

        await InsertUserAsync(first, "shared@test.local");

        var other = await Record.ExceptionAsync(() => InsertUserAsync(second, "shared@test.local"));

        Assert.Null(other);
    }

    private async Task<long> InsertScopeAsync()
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(
            "INSERT INTO heimdall.scope (public_id, name) VALUES (@publicId, @name) RETURNING id",
            connection);
        command.Parameters.AddWithValue("publicId", Guid.NewGuid());
        command.Parameters.AddWithValue("name", $"schema-{Guid.NewGuid():N}");

        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task InsertUserAsync(long scopeId, string email, bool isDeleted = false)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(
            """
            INSERT INTO heimdall.person
                (public_id, name, email, password_hash, salt, role_id, scope_id, is_deleted)
            VALUES (@publicId, 'Schema Test', @email, @hash, @salt, 3, @scopeId, @isDeleted)
            """,
            connection);
        command.Parameters.AddWithValue("publicId", Guid.NewGuid());
        command.Parameters.AddWithValue("email", email);
        command.Parameters.AddWithValue("hash", new byte[] { 0 });
        command.Parameters.AddWithValue("salt", new byte[] { 0 });
        command.Parameters.AddWithValue("scopeId", scopeId);
        command.Parameters.AddWithValue("isDeleted", isDeleted);

        await command.ExecuteNonQueryAsync();
    }

    private async Task<List<string>> ReadTableNamesAsync()
    {
        var names = new List<string>();

        await using var connection = new NpgsqlConnection(fixture.ConnectionString);

        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(
            """
            select table_name
            from information_schema.tables
            where table_schema = 'heimdall' and table_name <> '__EFMigrationsHistory'
            order by table_name
            """,
            connection);

        await using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }
}
