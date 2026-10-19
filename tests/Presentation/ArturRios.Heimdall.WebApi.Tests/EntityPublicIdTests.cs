using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.WebApi.Tests.Support;
using ArturRios.Util.Test.Attributes;
using Npgsql;

namespace ArturRios.Heimdall.WebApi.Tests;

/// <summary>
///     <c>public_id</c> on every table the identity check trusts — <c>scope</c>, <c>application</c>,
///     <c>application_credential</c>, <c>scope_permission</c>, <c>role</c>,
///     <c>pending_registration</c> and <c>scope_owner_invitation</c> — is immutable, and no guarded table, the identity tables included,
///     takes an id another one holds (#199, R2 and R3), against real PostgreSQL.
/// </summary>
/// <remarks>
///     <para>
///         The identity check can only see who holds an id at the moment it runs. While the other
///         tables' ids could be rewritten it could be walked round: rename a scope's id, insert a
///         person with the freed id, anonymise them, clear every audit target naming the scope, then
///         rename the scope back. And while only identity inserts were checked, a person inserted
///         first could share its id with a scope inserted after it. Both are pinned here: the rename
///         is refused and the chain leaves the entry as it was, and every one of the nine tables
///         refuses an id another holds.
///     </para>
///     <para>
///         Each table also gets an ordinary update and a fresh-id insert that pass, so a test cannot
///         pass on a trigger that refuses everything. Inserts that could leave a row in a table other
///         tests share, the role table above all, run in a transaction that is rolled back.
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
    public async Task GivenAScopeOwnerInvitation_WhenItsPublicIdIsRewritten_ThenTheDatabaseRefuses()
    {
        // Given
        var with = await SeedDependenciesAsync();
        var publicId = Guid.NewGuid();
        Assert.Null(await RunAsync(InsertStatement("scope_owner_invitation", publicId, with)));

        // When / Then
        await AssertRewriteRefusedAsync("scope_owner_invitation", publicId);
    }

    [FunctionalFact]
    public async Task GivenAScopeOwnerInvitation_WhenAnotherColumnIsUpdated_ThenTheDatabasePermitsIt()
    {
        // Given
        var with = await SeedDependenciesAsync();
        var publicId = Guid.NewGuid();
        Assert.Null(await RunAsync(InsertStatement("scope_owner_invitation", publicId, with)));

        // When / Then — a withdrawal, as UC-58 ends one
        await AssertOrdinaryUpdatePassesAsync("scope_owner_invitation", publicId, "ended_at = now(), outcome = 3");
    }

    /// <summary>The rows a test row of any guarded table depends on.</summary>
    private sealed record Dependencies(long ScopeId, long ApplicationId, long OwnerId);

    private async Task<Dependencies> SeedDependenciesAsync()
    {
        var application = await SeedApplicationAsync();

        return new Dependencies(application.ScopeId, application.Id, application.OwnerId);
    }

    /// <summary>An INSERT of one row with the given <c>public_id</c> into any of the nine guarded tables.</summary>
    private static string InsertStatement(string table, Guid publicId, Dependencies with) => table switch
    {
        "person" => $"""
                     INSERT INTO heimdall.person (public_id, name, email, password_hash, salt, role_id)
                     VALUES ('{publicId}', 'Entity Public Id', '{Unique("entity-public-id")}@functional.test',
                             '\x01', '\x02', {(long)Roles.User})
                     """,
        "google_user" => $"""
                          INSERT INTO heimdall.google_user (public_id, google_id, name, email, scope_id)
                          VALUES ('{publicId}', '{Guid.NewGuid():N}', 'Entity Public Id',
                                  '{Unique("entity-public-id")}@functional.test', {with.ScopeId})
                          """,
        "scope" => $"INSERT INTO heimdall.scope (public_id, name) VALUES ('{publicId}', '{Unique("entity-public-id")}')",
        "application" => $"""
                          INSERT INTO heimdall.application (public_id, name, scope_id, owner_id)
                          VALUES ('{publicId}', '{Unique("entity-public-id")}', {with.ScopeId}, {with.OwnerId})
                          """,
        "application_credential" => $"""
                                     INSERT INTO heimdall.application_credential
                                         (public_id, application_id, secret_hash, created_at)
                                     VALUES ('{publicId}', {with.ApplicationId}, '{Guid.NewGuid():N}{Guid.NewGuid():N}', now())
                                     """,
        "scope_permission" => $"""
                               INSERT INTO heimdall.scope_permission (public_id, name, scope_id)
                               VALUES ('{publicId}', '{Unique("entity-public-id")}', {with.ScopeId})
                               """,
        "role" => $"""
                   INSERT INTO heimdall.role (id, public_id, name)
                   VALUES ((SELECT max(id) + 1000 FROM heimdall.role), '{publicId}', '{Unique("role")}')
                   """,
        "pending_registration" => $"""
                                   INSERT INTO heimdall.pending_registration
                                       (public_id, scope_id, name, email, password_hash, salt, token_hash, expires_at)
                                   VALUES ('{publicId}', {with.ScopeId}, 'Entity Public Id',
                                           '{Unique("entity-public-id")}@functional.test', '\x01', '\x02',
                                           '{Guid.NewGuid():N}{Guid.NewGuid():N}', now() + interval '1 day')
                                   """,
        "scope_owner_invitation" => $"""
                                     INSERT INTO heimdall.scope_owner_invitation
                                         (public_id, scope_id, invitee_person_id, created_at, expires_at)
                                     VALUES ('{publicId}', {with.ScopeId}, {with.OwnerId}, now(), now() + interval '7 days')
                                     """,
        _ => throw new ArgumentOutOfRangeException(nameof(table), table, null)
    };

    /// <summary>
    ///     A committed row of the given table, holding a fresh id. A role is the seeder's own, so the
    ///     shared role table never gains a test row.
    /// </summary>
    private async Task<Guid> SeedHolderAsync(string table, Dependencies with)
    {
        if (table == "role")
        {
            return await SeededRolePublicIdAsync();
        }

        var publicId = Guid.NewGuid();
        Assert.Null(await RunAsync(InsertStatement(table, publicId, with)));

        return publicId;
    }

    /// <summary>
    ///     Runs the statement inside a transaction that is always rolled back, so an insert that is
    ///     permitted, into <c>role</c> above all, leaves nothing behind. Returns the failure, if any,
    ///     and how many rows of the table held the id inside the transaction.
    /// </summary>
    private async Task<(Exception? Failure, long Inside)> RunRolledBackAsync(string statement, string table, Guid publicId)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        await using var command = new NpgsqlCommand(statement, connection, transaction);
        var failure = await Record.ExceptionAsync(() => command.ExecuteNonQueryAsync());

        long inside = 0;

        if (failure is null)
        {
            await using var count = new NpgsqlCommand(
                $"SELECT count(*) FROM heimdall.{table} WHERE public_id = '{publicId}'", connection, transaction);
            inside = (long)(await count.ExecuteScalarAsync())!;
        }

        await transaction.RollbackAsync();

        return (failure, inside);
    }

    [FunctionalTheory]
    [InlineData("person", "pending_registration")]
    [InlineData("google_user", "role")]
    [InlineData("scope", "person")]
    [InlineData("application", "google_user")]
    [InlineData("application_credential", "scope_permission")]
    [InlineData("scope_permission", "application_credential")]
    [InlineData("role", "application")]
    [InlineData("pending_registration", "scope_owner_invitation")]
    [InlineData("scope_owner_invitation", "scope")]
    public async Task GivenAnIdAnotherTableHolds_WhenARowIsInsertedWithIt_ThenTheDatabaseRefuses(string table, string holder)
    {
        // Given — every table is inserted into once and holds an id once, so every branch of the check is
        // exercised from both sides (R3, I2)
        var with = await SeedDependenciesAsync();
        var held = await SeedHolderAsync(holder, with);

        // When
        var (failure, _) = await RunRolledBackAsync(InsertStatement(table, held, with), table, held);

        // Then
        var refusal = Assert.IsType<PostgresException>(failure);
        Assert.Equal(RestrictViolation, refusal.SqlState);
        Assert.Contains($"heimdall.{table} public_id is already used by another entity", refusal.MessageText);
        Assert.Equal(0, await CountAsync(table, held));
    }

    [FunctionalTheory]
    [InlineData("person")]
    [InlineData("google_user")]
    [InlineData("scope")]
    [InlineData("application")]
    [InlineData("application_credential")]
    [InlineData("scope_permission")]
    [InlineData("role")]
    [InlineData("pending_registration")]
    [InlineData("scope_owner_invitation")]
    public async Task GivenAFreshId_WhenARowIsInsertedWithIt_ThenTheDatabasePermitsIt(string table)
    {
        // Given
        var with = await SeedDependenciesAsync();
        var fresh = Guid.NewGuid();

        // When
        var (failure, inside) = await RunRolledBackAsync(InsertStatement(table, fresh, with), table, fresh);

        // Then
        Assert.Null(failure);
        Assert.Equal(1, inside);
    }

    [FunctionalFact]
    public async Task GivenATombstonedId_WhenAScopeIsInsertedWithIt_ThenTheDatabaseRefuses()
    {
        // Given — the tombstone path (R4, N1): plant a person, tombstone it far in the future so the pass
        // never settles it, delete the person. A scope on that id would have every entry about it cleared.
        var with = await SeedDependenciesAsync();
        var tombstoned = Guid.NewGuid();
        Assert.Null(await RunAsync(InsertStatement("person", tombstoned, with)));
        Assert.Null(await RunAsync(
            $"""
             INSERT INTO heimdall.erased_subject (public_id, kind, created_at)
             VALUES ('{tombstoned}', {(int)ErasedSubjectKinds.Person}, '9999-01-01')
             """));
        Assert.Null(await RunAsync($"DELETE FROM heimdall.person WHERE public_id = '{tombstoned}'"));

        try
        {
            // When
            var (failure, _) = await RunRolledBackAsync(InsertStatement("scope", tombstoned, with), "scope", tombstoned);

            // Then
            var refusal = Assert.IsType<PostgresException>(failure);
            Assert.Equal(RestrictViolation, refusal.SqlState);
            Assert.Contains("heimdall.scope public_id is already used by another entity", refusal.MessageText);
            Assert.Equal(0, await CountAsync("scope", tombstoned));
        }
        finally
        {
            // A tombstone dated in the future is never settled by the pass; leave none behind
            await RunAsync($"DELETE FROM heimdall.erased_subject WHERE public_id = '{tombstoned}'");
        }
    }

    [FunctionalFact]
    public async Task GivenAScopeNamedInTheAuditTrail_WhenTheRenameThenReuseChainIsAttempted_ThenTheTargetIsStillTheScope()
    {
        // Given — a scope an audit entry names, past every floor; not personal data, so it must stay
        var scope = await SeedScopeAsync();
        var entry = Guid.NewGuid();
        Assert.Null(await RunAsync(
            $"""
             INSERT INTO heimdall.audit_log (public_id, action, succeeded, target_id, created_at)
             VALUES ('{entry}', 'EntityPublicIdTestCommand', true, '{scope.PublicId}', now() - interval '400 days')
             """));
        var freed = Guid.NewGuid();

        // When — the whole chain: move the scope off its id, plant an anonymised person with it, clear
        // the target, move the scope back. Each step is attempted whatever the one before did.
        var rename = await RunAsync(
            $"UPDATE heimdall.scope SET public_id = '{freed}' WHERE public_id = '{scope.PublicId}'");
        await RunAsync(
            $"""
             INSERT INTO heimdall.person (public_id, name, email, password_hash, salt, role_id, anonymised_at)
             VALUES ('{scope.PublicId}', 'Entity Public Id', '{Unique("entity-public-id")}@functional.test',
                     '\x01', '\x02', {(long)Roles.User}, now())
             """);
        await RunAsync($"UPDATE heimdall.audit_log SET target_id = NULL WHERE public_id = '{entry}'");
        await RunAsync($"UPDATE heimdall.scope SET public_id = '{scope.PublicId}' WHERE public_id = '{freed}'");

        // Then — the first step is refused, and the end state is what it was: the entry still names the scope
        var refusal = Assert.IsType<PostgresException>(rename);
        Assert.Equal(RestrictViolation, refusal.SqlState);
        Assert.Contains("heimdall.scope public_id is immutable", refusal.MessageText);

        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var target = new NpgsqlCommand(
            $"SELECT target_id FROM heimdall.audit_log WHERE public_id = '{entry}'", connection);
        Assert.Equal(scope.PublicId, (Guid)(await target.ExecuteScalarAsync())!);
        Assert.Equal(1, await CountAsync("scope", scope.PublicId));
        Assert.Equal(0, await CountAsync("person", scope.PublicId));
    }
}
