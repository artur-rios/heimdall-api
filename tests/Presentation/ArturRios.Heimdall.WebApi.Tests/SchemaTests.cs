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
            "email_verification_token",
            "google_user",
            "password_reset_token",
            "person",
            "role",
            "scope",
            "scope_owner",
            "scope_permission",
            "scope_user"
        ];

        var actual = await ReadTableNamesAsync();

        Assert.Equal(expected, actual);
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
