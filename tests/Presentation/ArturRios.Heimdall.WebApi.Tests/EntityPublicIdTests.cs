using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.WebApi.Tests.Support;
using ArturRios.Util.Test.Attributes;
using Npgsql;

namespace ArturRios.Heimdall.WebApi.Tests;

/// <summary>
///     <c>public_id</c> is immutable on every table the identity insert check consults (#199, R2):
///     <c>scope</c>, <c>application</c>, <c>application_credential</c>, <c>scope_permission</c>,
///     <c>role</c> and <c>pending_registration</c>, against real PostgreSQL.
/// </summary>
/// <remarks>
///     <para>
///         The insert check refuses a new person or Google User whose <c>public_id</c> another
///         entity holds, but only at that moment. While the other tables' ids could be rewritten, it
///         could be walked round: rename a scope's id, insert a person with the freed id, anonymise
///         them, clear every audit target naming the scope, then rename the scope back. With the id
///         fixed everywhere the check looks, the first step is refused and the chain cannot start.
///     </para>
///     <para>
///         Each table gets a refusal and an ordinary update that passes, so a test cannot pass on a
///         trigger that refuses everything.
///     </para>
/// </remarks>
[Collection(nameof(FunctionalCollection))]
public class EntityPublicIdTests(PostgresFixture fixture)
{
    private const string RestrictViolation = "23001";

    private async Task<Exception?> RunAsync(string statement)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(statement, connection);

        return await Record.ExceptionAsync(() => command.ExecuteNonQueryAsync());
    }

    private async Task<long> CountAsync(string table, Guid publicId)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            $"SELECT count(*) FROM heimdall.{table} WHERE public_id = '{publicId}'", connection);

        return (long)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>Rewrites the row's <c>public_id</c> and asserts the trigger refused it, leaving the row as it was.</summary>
    private async Task AssertRewriteRefusedAsync(string table, Guid publicId)
    {
        var replacement = Guid.NewGuid();

        var failure = await RunAsync(
            $"UPDATE heimdall.{table} SET public_id = '{replacement}' WHERE public_id = '{publicId}'");

        var refusal = Assert.IsType<PostgresException>(failure);
        Assert.Equal(RestrictViolation, refusal.SqlState);
        Assert.Contains($"heimdall.{table} public_id is immutable", refusal.MessageText);
        Assert.Equal(1, await CountAsync(table, publicId));
        Assert.Equal(0, await CountAsync(table, replacement));
    }

    /// <summary>Applies an update that leaves <c>public_id</c> alone and asserts it passed.</summary>
    private async Task AssertOrdinaryUpdatePassesAsync(string table, Guid publicId, string assignments)
    {
        var failure = await RunAsync($"UPDATE heimdall.{table} SET {assignments} WHERE public_id = '{publicId}'");

        Assert.Null(failure);
        Assert.Equal(1, await CountAsync(table, publicId));
    }

    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    private async Task<Scope> SeedScopeAsync()
    {
        await using var context = fixture.CreateContext();
        var scope = new Scope { PublicId = Guid.NewGuid(), Name = Unique("entity-public-id") };
        context.Scopes.Add(scope);
        await context.SaveChangesAsync();

        return scope;
    }

    private async Task<Application> SeedApplicationAsync()
    {
        var scope = await SeedScopeAsync();

        await using var context = fixture.CreateContext();
        var owner = new Person
        {
            PublicId = Guid.NewGuid(),
            Name = "Entity Public Id",
            Email = $"{Unique("entity-public-id")}@functional.test",
            PasswordHash = [1],
            Salt = [2],
            RoleId = (long)Roles.User
        };
        context.Persons.Add(owner);
        await context.SaveChangesAsync();

        var application = new Application
        {
            PublicId = Guid.NewGuid(), Name = Unique("entity-public-id"), ScopeId = scope.Id, OwnerId = owner.Id
        };
        context.Applications.Add(application);
        await context.SaveChangesAsync();

        return application;
    }

    private async Task<ApplicationCredential> SeedCredentialAsync()
    {
        var application = await SeedApplicationAsync();

        await using var context = fixture.CreateContext();
        var credential = new ApplicationCredential
        {
            PublicId = Guid.NewGuid(),
            ApplicationId = application.Id,
            SecretHash = $"{Guid.NewGuid():N}{Guid.NewGuid():N}",
            CreatedAt = DateTime.UtcNow
        };
        context.ApplicationCredentials.Add(credential);
        await context.SaveChangesAsync();

        return credential;
    }

    private async Task<ScopePermission> SeedPermissionAsync()
    {
        var scope = await SeedScopeAsync();

        await using var context = fixture.CreateContext();
        var permission = new ScopePermission
        {
            PublicId = Guid.NewGuid(), Name = Unique("entity-public-id"), ScopeId = scope.Id
        };
        context.ScopePermissions.Add(permission);
        await context.SaveChangesAsync();

        return permission;
    }

    private async Task<PendingRegistration> SeedPendingRegistrationAsync()
    {
        var scope = await SeedScopeAsync();

        await using var context = fixture.CreateContext();
        var now = DateTime.UtcNow;
        var pending = new PendingRegistration
        {
            PublicId = Guid.NewGuid(),
            ScopeId = scope.Id,
            Name = "Entity Public Id",
            Email = $"{Unique("entity-public-id")}@functional.test",
            PasswordHash = [1],
            Salt = [2],
            TokenHash = $"{Guid.NewGuid():N}{Guid.NewGuid():N}",
            CreatedAt = now,
            ExpiresAt = now.AddDays(1)
        };
        context.PendingRegistrations.Add(pending);
        await context.SaveChangesAsync();

        return pending;
    }

    private async Task<Guid> SeededRolePublicIdAsync()
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            $"SELECT public_id FROM heimdall.role WHERE id = {(long)Roles.User}", connection);

        return (Guid)(await command.ExecuteScalarAsync())!;
    }

    [FunctionalFact]
    public async Task GivenAScope_WhenItsPublicIdIsRewritten_ThenTheDatabaseRefuses()
    {
        // Given
        var scope = await SeedScopeAsync();

        // When / Then
        await AssertRewriteRefusedAsync("scope", scope.PublicId);
    }

    [FunctionalFact]
    public async Task GivenAScope_WhenAnotherColumnIsUpdated_ThenTheDatabasePermitsIt()
    {
        // Given
        var scope = await SeedScopeAsync();

        // When / Then
        await AssertOrdinaryUpdatePassesAsync("scope", scope.PublicId, $"name = '{Unique("renamed")}', is_deleted = true");
    }

    [FunctionalFact]
    public async Task GivenAnApplication_WhenItsPublicIdIsRewritten_ThenTheDatabaseRefuses()
    {
        // Given
        var application = await SeedApplicationAsync();

        // When / Then
        await AssertRewriteRefusedAsync("application", application.PublicId);
    }

    [FunctionalFact]
    public async Task GivenAnApplication_WhenAnotherColumnIsUpdated_ThenTheDatabasePermitsIt()
    {
        // Given
        var application = await SeedApplicationAsync();

        // When / Then
        await AssertOrdinaryUpdatePassesAsync("application", application.PublicId, $"name = '{Unique("renamed")}'");
    }

    [FunctionalFact]
    public async Task GivenAnApplicationCredential_WhenItsPublicIdIsRewritten_ThenTheDatabaseRefuses()
    {
        // Given
        var credential = await SeedCredentialAsync();

        // When / Then
        await AssertRewriteRefusedAsync("application_credential", credential.PublicId);
    }

    [FunctionalFact]
    public async Task GivenAnApplicationCredential_WhenAnotherColumnIsUpdated_ThenTheDatabasePermitsIt()
    {
        // Given
        var credential = await SeedCredentialAsync();

        // When / Then — revoking, as UC-55 does
        await AssertOrdinaryUpdatePassesAsync("application_credential", credential.PublicId, "revoked_at = now()");
    }

    [FunctionalFact]
    public async Task GivenAScopePermission_WhenItsPublicIdIsRewritten_ThenTheDatabaseRefuses()
    {
        // Given
        var permission = await SeedPermissionAsync();

        // When / Then
        await AssertRewriteRefusedAsync("scope_permission", permission.PublicId);
    }

    [FunctionalFact]
    public async Task GivenAScopePermission_WhenAnotherColumnIsUpdated_ThenTheDatabasePermitsIt()
    {
        // Given
        var permission = await SeedPermissionAsync();

        // When / Then
        await AssertOrdinaryUpdatePassesAsync(
            "scope_permission", permission.PublicId, "description = 'Updated', include_as_jwt_claim = true");
    }

    [FunctionalFact]
    public async Task GivenARole_WhenItsPublicIdIsRewritten_ThenTheDatabaseRefuses()
    {
        // Given — a role the seeder created
        var role = await SeededRolePublicIdAsync();

        // When / Then
        await AssertRewriteRefusedAsync("role", role);
    }

    [FunctionalFact]
    public async Task GivenARole_WhenAnotherColumnIsUpdated_ThenTheDatabasePermitsIt()
    {
        // Given — the seeder's realignment writes name and description; this writes them back unchanged,
        // so no other test sees a different role
        var role = await SeededRolePublicIdAsync();

        // When / Then
        await AssertOrdinaryUpdatePassesAsync("role", role, "name = name, description = description");
    }

    [FunctionalFact]
    public async Task GivenAPendingRegistration_WhenItsPublicIdIsRewritten_ThenTheDatabaseRefuses()
    {
        // Given
        var pending = await SeedPendingRegistrationAsync();

        // When / Then
        await AssertRewriteRefusedAsync("pending_registration", pending.PublicId);
    }

    [FunctionalFact]
    public async Task GivenAPendingRegistration_WhenAnotherColumnIsUpdated_ThenTheDatabasePermitsIt()
    {
        // Given
        var pending = await SeedPendingRegistrationAsync();

        // When / Then — a wrong confirmation guess, as UC-47 counts it
        await AssertOrdinaryUpdatePassesAsync(
            "pending_registration", pending.PublicId, "failed_attempts = failed_attempts + 1");
    }

    [FunctionalFact]
    public async Task GivenAScopeNamedInTheAuditTrail_WhenRenamedToFreeItsIdForAPerson_ThenTheChainCannotStart()
    {
        // Given — a scope an audit entry names, which is not personal data and must stay as recorded
        var scope = await SeedScopeAsync();
        var entry = Guid.NewGuid();
        Assert.Null(await RunAsync(
            $"""
             INSERT INTO heimdall.audit_log (public_id, action, succeeded, target_id, created_at)
             VALUES ('{entry}', 'EntityPublicIdTestCommand', true, '{scope.PublicId}', now() - interval '400 days')
             """));

        // When — the bypass: move the scope off its id, so a person could be inserted with it
        var rename = await RunAsync(
            $"UPDATE heimdall.scope SET public_id = '{Guid.NewGuid()}' WHERE public_id = '{scope.PublicId}'");

        // Then — the first step is refused, so the id stays the scope's, a person cannot take it, and
        // the entry's target cannot be cleared
        var refusal = Assert.IsType<PostgresException>(rename);
        Assert.Equal(RestrictViolation, refusal.SqlState);
        Assert.Contains("heimdall.scope public_id is immutable", refusal.MessageText);

        var insert = await RunAsync(
            $"""
             INSERT INTO heimdall.person (public_id, name, email, password_hash, salt, role_id, anonymised_at)
             VALUES ('{scope.PublicId}', 'Entity Public Id', '{Unique("entity-public-id")}@functional.test',
                     '\x01', '\x02', 3, now())
             """);
        Assert.Contains("heimdall.person public_id is already used by another entity",
            Assert.IsType<PostgresException>(insert).MessageText);

        var clearing = await RunAsync($"UPDATE heimdall.audit_log SET target_id = NULL WHERE public_id = '{entry}'");
        Assert.Contains("names no person", Assert.IsType<PostgresException>(clearing).MessageText);
        Assert.Equal(1, await CountAsync("scope", scope.PublicId));
    }
}
