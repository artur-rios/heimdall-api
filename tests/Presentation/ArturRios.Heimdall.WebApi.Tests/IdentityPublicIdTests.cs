using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.WebApi.Tests.Support;
using ArturRios.Util.Test.Attributes;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ArturRios.Heimdall.WebApi.Tests;

/// <summary>
///     The identity tables' <c>public_id</c> means what it says (#199): it can never be rewritten,
///     and a new person or Google User cannot take an id another entity holds or an audit entry
///     names, against real PostgreSQL.
/// </summary>
/// <remarks>
///     <para>
///         The audit trail's trigger lets a target be cleared because it is a person's or a Google
///         User's id, and the tombstone guard accepts an id because a person or Google User has it
///         (#184, #195). Both read that from the identity rows, so a row given a scope's, an
///         application's or a credential's id — by rewriting it, or by inserting a new one — made
///         targets that never named a person clearable. These tests pin the two triggers that close
///         it, each refusal by its own reason, and that every ordinary write still passes.
///     </para>
///     <para>
///         "Already held" means by a row that is there, soft-deleted or not; no registry of issued
///         ids is kept, as it would keep a removed person's id for good. A hard-deleted entity's id
///         is still refused while the audit trail names it (R3, I1), which is the only place a
///         removed entity's id still matters.
///     </para>
/// </remarks>
[Collection(nameof(FunctionalCollection))]
public class IdentityPublicIdTests(PostgresFixture fixture)
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

    /// <summary>Asserts a trigger refused the statement, for the reason given.</summary>
    private static void AssertRefused(Exception? failure, string reason)
    {
        var refusal = Assert.IsType<PostgresException>(failure);

        Assert.Equal(RestrictViolation, refusal.SqlState);
        Assert.Contains(reason, refusal.MessageText);
    }

    private static string InsertPersonStatement(Guid publicId) =>
        $"""
         INSERT INTO heimdall.person (public_id, name, email, password_hash, salt, role_id)
         VALUES ('{publicId}', 'Identity Public Id', 'identity-public-id-{Guid.NewGuid():N}@functional.test',
                 '\x01', '\x02', 3)
         """;

    private static string InsertGoogleUserStatement(Guid publicId, long scopeId) =>
        $"""
         INSERT INTO heimdall.google_user (public_id, google_id, name, email, scope_id)
         VALUES ('{publicId}', '{Guid.NewGuid():N}', 'Identity Public Id',
                 'identity-public-id-{Guid.NewGuid():N}@functional.test', {scopeId})
         """;

    private async Task<Scope> SeedScopeAsync(bool isDeleted = false)
    {
        await using var context = fixture.CreateContext();
        var scope = new Scope
        {
            PublicId = Guid.NewGuid(), Name = $"identity-public-id-{Guid.NewGuid():N}", IsDeleted = isDeleted
        };
        context.Scopes.Add(scope);
        await context.SaveChangesAsync();

        return scope;
    }

    private async Task<Person> SeedPersonAsync()
    {
        await using var context = fixture.CreateContext();
        var person = new Person
        {
            PublicId = Guid.NewGuid(),
            Name = "Identity Public Id",
            Email = $"identity-public-id-{Guid.NewGuid():N}@functional.test",
            PasswordHash = [1],
            Salt = [2],
            RoleId = (long)Roles.User
        };
        context.Persons.Add(person);
        await context.SaveChangesAsync();

        return person;
    }

    private async Task<GoogleUser> SeedGoogleUserAsync()
    {
        var scope = await SeedScopeAsync();

        await using var context = fixture.CreateContext();
        var googleUser = new GoogleUser
        {
            PublicId = Guid.NewGuid(),
            GoogleId = Guid.NewGuid().ToString("N"),
            Name = "Identity Public Id",
            Email = $"identity-public-id-{Guid.NewGuid():N}@functional.test",
            ScopeId = scope.Id
        };
        context.GoogleUsers.Add(googleUser);
        await context.SaveChangesAsync();

        return googleUser;
    }

    private async Task<Application> SeedApplicationAsync()
    {
        var scope = await SeedScopeAsync();
        var owner = await SeedPersonAsync();

        await using var context = fixture.CreateContext();
        var application = new Application
        {
            PublicId = Guid.NewGuid(),
            Name = $"identity-public-id-{Guid.NewGuid():N}",
            ScopeId = scope.Id,
            OwnerId = owner.Id
        };
        context.Applications.Add(application);
        await context.SaveChangesAsync();

        return application;
    }

    [FunctionalFact]
    public async Task GivenAPerson_WhenItsPublicIdIsRewritten_ThenTheDatabaseRefuses()
    {
        // Given — a scope whose id somebody wants a person to hold
        var person = await SeedPersonAsync();
        var scope = await SeedScopeAsync();

        // When
        var failure = await RunAsync(
            $"UPDATE heimdall.person SET public_id = '{scope.PublicId}' WHERE public_id = '{person.PublicId}'");

        // Then
        AssertRefused(failure, "heimdall.person public_id is immutable");
        Assert.Equal(1, await CountAsync("person", person.PublicId));
        Assert.Equal(0, await CountAsync("person", scope.PublicId));
    }

    [FunctionalFact]
    public async Task GivenAGoogleUser_WhenItsPublicIdIsRewritten_ThenTheDatabaseRefuses()
    {
        // Given
        var googleUser = await SeedGoogleUserAsync();
        var scope = await SeedScopeAsync();

        // When
        var failure = await RunAsync(
            $"UPDATE heimdall.google_user SET public_id = '{scope.PublicId}' WHERE public_id = '{googleUser.PublicId}'");

        // Then
        AssertRefused(failure, "heimdall.google_user public_id is immutable");
        Assert.Equal(1, await CountAsync("google_user", googleUser.PublicId));
        Assert.Equal(0, await CountAsync("google_user", scope.PublicId));
    }

    [FunctionalFact]
    public async Task GivenAPerson_WhenItsNameEmailAndAnonymisedAtAreUpdated_ThenTheDatabasePermitsIt()
    {
        // Given
        var person = await SeedPersonAsync();
        var email = $"identity-public-id-renamed-{Guid.NewGuid():N}@functional.test";

        // When — every column but public_id may change, as anonymisation and a soft delete change them
        var failure = await RunAsync(
            $"""
             UPDATE heimdall.person
             SET name = 'Renamed', email = '{email}', anonymised_at = now(), is_deleted = true
             WHERE public_id = '{person.PublicId}'
             """);

        // Then
        Assert.Null(failure);

        await using var context = fixture.CreateContext();
        var stored = await context.Persons.AsNoTracking().SingleAsync(x => x.PublicId == person.PublicId);
        Assert.Equal("Renamed", stored.Name);
        Assert.Equal(email, stored.Email);
        Assert.NotNull(stored.AnonymisedAt);
        Assert.True(stored.IsDeleted);
    }

    [FunctionalFact]
    public async Task GivenAPerson_WhenUpdatedThroughEfWithEveryColumnMarked_ThenTheDatabasePermitsIt()
    {
        // Given — Update() marks every property modified, so the statement writes public_id back as it was
        var person = await SeedPersonAsync();
        person.Name = "Updated Through EF";

        // When
        Exception? failure;

        await using (var context = fixture.CreateContext())
        {
            context.Persons.Update(person);
            failure = await Record.ExceptionAsync(() => context.SaveChangesAsync());
        }

        // Then
        Assert.Null(failure);

        await using var check = fixture.CreateContext();
        var stored = await check.Persons.AsNoTracking().SingleAsync(x => x.PublicId == person.PublicId);
        Assert.Equal("Updated Through EF", stored.Name);
    }

    [FunctionalFact]
    public async Task GivenAGoogleUser_WhenUpdatedThroughEfWithEveryColumnMarked_ThenTheDatabasePermitsIt()
    {
        // Given
        var googleUser = await SeedGoogleUserAsync();
        googleUser.Name = "Updated Through EF";

        // When
        Exception? failure;

        await using (var context = fixture.CreateContext())
        {
            context.GoogleUsers.Update(googleUser);
            failure = await Record.ExceptionAsync(() => context.SaveChangesAsync());
        }

        // Then
        Assert.Null(failure);

        await using var check = fixture.CreateContext();
        var stored = await check.GoogleUsers.AsNoTracking().SingleAsync(x => x.PublicId == googleUser.PublicId);
        Assert.Equal("Updated Through EF", stored.Name);
    }

    [FunctionalFact]
    public async Task GivenALiveScopesId_WhenAPersonIsInsertedWithIt_ThenTheDatabaseRefuses()
    {
        // Given — the attack #199 closes: a person holding a scope's id makes its targets clearable
        var scope = await SeedScopeAsync();

        // When
        var failure = await RunAsync(InsertPersonStatement(scope.PublicId));

        // Then
        AssertRefused(failure, "heimdall.person public_id is already used by another entity");
        Assert.Equal(0, await CountAsync("person", scope.PublicId));
    }

    [FunctionalFact]
    public async Task GivenASoftDeletedScopesId_WhenAPersonIsInsertedWithIt_ThenTheDatabaseRefuses()
    {
        // Given — a soft-deleted scope is still there, and so are the entries naming it
        var scope = await SeedScopeAsync(isDeleted: true);

        // When
        var failure = await RunAsync(InsertPersonStatement(scope.PublicId));

        // Then
        AssertRefused(failure, "heimdall.person public_id is already used by another entity");
        Assert.Equal(0, await CountAsync("person", scope.PublicId));
    }

    [FunctionalFact]
    public async Task GivenAnApplicationsId_WhenAPersonIsInsertedWithIt_ThenTheDatabaseRefuses()
    {
        // Given
        var application = await SeedApplicationAsync();

        // When
        var failure = await RunAsync(InsertPersonStatement(application.PublicId));

        // Then
        AssertRefused(failure, "heimdall.person public_id is already used by another entity");
        Assert.Equal(0, await CountAsync("person", application.PublicId));
    }

    [FunctionalFact]
    public async Task GivenAGoogleUsersId_WhenAPersonIsInsertedWithIt_ThenTheDatabaseRefuses()
    {
        // Given
        var googleUser = await SeedGoogleUserAsync();

        // When
        var failure = await RunAsync(InsertPersonStatement(googleUser.PublicId));

        // Then
        AssertRefused(failure, "heimdall.person public_id is already used by another entity");
        Assert.Equal(0, await CountAsync("person", googleUser.PublicId));
    }

    [FunctionalFact]
    public async Task GivenAPersonsId_WhenAGoogleUserIsInsertedWithIt_ThenTheDatabaseRefuses()
    {
        // Given
        var person = await SeedPersonAsync();
        var scope = await SeedScopeAsync();

        // When
        var failure = await RunAsync(InsertGoogleUserStatement(person.PublicId, scope.Id));

        // Then
        AssertRefused(failure, "heimdall.google_user public_id is already used by another entity");
        Assert.Equal(0, await CountAsync("google_user", person.PublicId));
    }

    /// <summary>
    ///     A scope hard-deleted as UC-05 does it, after an audit entry about it was written: no row
    ///     holds its id any more, but the trail still names it.
    /// </summary>
    private async Task<Guid> HardDeletedScopeTheTrailNamesAsync()
    {
        var scope = await SeedScopeAsync();

        Assert.Null(await RunAsync(
            $"""
             INSERT INTO heimdall.audit_log (public_id, action, succeeded, target_id)
             VALUES ('{Guid.NewGuid()}', 'IdentityPublicIdTestCommand', true, '{scope.PublicId}')
             """));
        Assert.Null(await RunAsync($"DELETE FROM heimdall.scope WHERE public_id = '{scope.PublicId}'"));

        return scope.PublicId;
    }

    [FunctionalFact]
    public async Task GivenAHardDeletedScopeTheTrailNames_WhenAPersonIsInsertedWithItsId_ThenTheDatabaseRefuses()
    {
        // Given — the delete-then-reinsert path (R3, I1): an anonymised person with this id would make
        // every entry about the scope clearable
        var scopeId = await HardDeletedScopeTheTrailNamesAsync();

        // When
        var failure = await RunAsync(InsertPersonStatement(scopeId));

        // Then
        AssertRefused(failure, "heimdall.person public_id is named by an audit entry");
        Assert.Equal(0, await CountAsync("person", scopeId));
    }

    [FunctionalFact]
    public async Task GivenAHardDeletedScopeTheTrailNames_WhenAGoogleUserIsInsertedWithItsId_ThenTheDatabaseRefuses()
    {
        // Given
        var scopeId = await HardDeletedScopeTheTrailNamesAsync();
        var scope = await SeedScopeAsync();

        // When
        var failure = await RunAsync(InsertGoogleUserStatement(scopeId, scope.Id));

        // Then
        AssertRefused(failure, "heimdall.google_user public_id is named by an audit entry");
        Assert.Equal(0, await CountAsync("google_user", scopeId));
    }

    [FunctionalFact]
    public async Task GivenAFreshId_WhenAPersonIsInsertedWithIt_ThenTheDatabasePermitsIt()
    {
        // Given
        var fresh = Guid.NewGuid();

        // When
        var failure = await RunAsync(InsertPersonStatement(fresh));

        // Then
        Assert.Null(failure);
        Assert.Equal(1, await CountAsync("person", fresh));
    }
}
