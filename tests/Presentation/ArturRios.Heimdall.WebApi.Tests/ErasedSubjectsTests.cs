using ArturRios.Heimdall.Data.Persistence;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.WebApi.Tests.Support;
using ArturRios.Util.Test.Attributes;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ArturRios.Heimdall.WebApi.Tests;

/// <summary>
///     The tombstone write every hard delete makes first (NFR-21, #195), and the guard on the table it
///     writes to, against real PostgreSQL.
/// </summary>
/// <remarks>
///     <para>
///         The unit tests run the hard-delete handlers over an in-memory stand-in that only promises the
///         contract. This proves the real upsert keeps it: a retried hard delete records the same id
///         again without failing on the primary key, and still leaves one row.
///     </para>
///     <para>
///         The guard (#195, R1) is what keeps a tombstone from becoming a way round the audit trail's
///         trigger: without it, anyone able to write to the table could tombstone a live scope's id and
///         then clear every target naming it. A tombstone may only name a person or Google User that
///         exists when it is written — which every hard delete satisfies, because it writes first.
///     </para>
/// </remarks>
[Collection(nameof(FunctionalCollection))]
public class ErasedSubjectsTests(PostgresFixture fixture)
{
    private const string RestrictViolation = "23001";
    private const string GuardReason = "erased_subject may only record a person or Google User that exists";

    private static ErasedSubject Tombstone(Guid id, ErasedSubjectKinds kind, DateTime createdAt) =>
        new() { Id = id, Kind = (int)kind, CreatedAt = createdAt };

    private async Task<List<ErasedSubject>> StoredAsync(params Guid[] ids)
    {
        await using var context = fixture.CreateContext();

        return await context.ErasedSubjects.AsNoTracking().Where(x => ids.Contains(x.Id)).ToListAsync();
    }

    private async Task<Exception?> RunAsync(string statement)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(statement, connection);

        return await Record.ExceptionAsync(() => command.ExecuteNonQueryAsync());
    }

    private static void AssertRefusedByTheGuard(Exception? failure)
    {
        var refusal = Assert.IsType<PostgresException>(failure);

        Assert.Equal(RestrictViolation, refusal.SqlState);
        Assert.Contains(GuardReason, refusal.MessageText);
    }

    private static string InsertStatement(Guid id, ErasedSubjectKinds kind) =>
        $"INSERT INTO heimdall.erased_subject (public_id, kind) VALUES ('{id}', {(int)kind})";

    private async Task<Scope> SeedScopeAsync()
    {
        await using var context = fixture.CreateContext();
        var scope = new Scope { PublicId = Guid.NewGuid(), Name = $"erased-subject-{Guid.NewGuid():N}" };
        context.Scopes.Add(scope);
        await context.SaveChangesAsync();

        return scope;
    }

    private async Task<Guid> SeedPersonAsync()
    {
        await using var context = fixture.CreateContext();
        var person = new Person
        {
            PublicId = Guid.NewGuid(),
            Name = "Erased Subject",
            Email = $"erased-subject-{Guid.NewGuid():N}@functional.test",
            PasswordHash = [1],
            Salt = [2],
            RoleId = (long)Roles.User
        };
        context.Persons.Add(person);
        await context.SaveChangesAsync();

        return person.PublicId;
    }

    private async Task<Guid> SeedGoogleUserAsync()
    {
        var scope = await SeedScopeAsync();

        await using var context = fixture.CreateContext();
        var googleUser = new GoogleUser
        {
            PublicId = Guid.NewGuid(),
            GoogleId = Guid.NewGuid().ToString("N"),
            Name = "Erased Subject",
            Email = $"erased-subject-{Guid.NewGuid():N}@functional.test",
            ScopeId = scope.Id
        };
        context.GoogleUsers.Add(googleUser);
        await context.SaveChangesAsync();

        return googleUser.PublicId;
    }

    [FunctionalFact]
    public async Task GivenNewIds_WhenRecorded_ThenEachIsStoredOnceWithItsKind()
    {
        // Given a person and a Google User about to be removed in one hard delete
        var person = await SeedPersonAsync();
        var googleUser = await SeedGoogleUserAsync();
        var now = DateTime.UtcNow;

        // When
        await using (var context = fixture.CreateContext())
        {
            await new ErasedSubjects(context).RecordAsync(
            [
                Tombstone(person, ErasedSubjectKinds.Person, now),
                Tombstone(googleUser, ErasedSubjectKinds.GoogleUser, now)
            ]);
        }

        // Then
        var stored = (await StoredAsync(person, googleUser)).ToDictionary(x => x.Id);
        Assert.Equal(2, stored.Count);
        Assert.Equal((int)ErasedSubjectKinds.Person, stored[person].Kind);
        Assert.Equal((int)ErasedSubjectKinds.GoogleUser, stored[googleUser].Kind);
    }

    [FunctionalFact]
    public async Task GivenAnIdAlreadyRecorded_WhenRecordedAgain_ThenItIsNotAnErrorAndOneRowRemains()
    {
        // Given a tombstone left by an attempt that then failed to delete the row, which is still there
        var person = await SeedPersonAsync();
        var earlier = DateTime.UtcNow.AddDays(-3);

        await using (var context = fixture.CreateContext())
        {
            await new ErasedSubjects(context).RecordAsync([Tombstone(person, ErasedSubjectKinds.Person, earlier)]);
        }

        // When the retry records it again — the upsert's UPDATE passes the guard, as the row exists
        var retry = DateTime.UtcNow;
        Exception? failure;

        await using (var context = fixture.CreateContext())
        {
            failure = await Record.ExceptionAsync(() =>
                new ErasedSubjects(context).RecordAsync([Tombstone(person, ErasedSubjectKinds.Person, retry)]));
        }

        // Then — no primary-key failure, one row, its grace counted from the retry
        Assert.Null(failure);
        var stored = Assert.Single(await StoredAsync(person));
        Assert.True(stored.CreatedAt > earlier.AddDays(1));
    }

    [FunctionalFact]
    public async Task GivenTheSameIdTwiceInOneCall_WhenRecorded_ThenOneRowIsStored()
    {
        // Given — ON CONFLICT cannot touch one row twice in a statement, so duplicates must not reach it
        var person = await SeedPersonAsync();
        var now = DateTime.UtcNow;

        // When
        await using (var context = fixture.CreateContext())
        {
            await new ErasedSubjects(context).RecordAsync(
            [
                Tombstone(person, ErasedSubjectKinds.Person, now),
                Tombstone(person, ErasedSubjectKinds.Person, now)
            ]);
        }

        // Then
        Assert.Single(await StoredAsync(person));
    }

    [FunctionalFact]
    public async Task GivenALivePerson_WhenATombstoneIsInserted_ThenTheDatabasePermitsIt()
    {
        // Given
        var person = await SeedPersonAsync();

        // When
        var failure = await RunAsync(InsertStatement(person, ErasedSubjectKinds.Person));

        // Then
        Assert.Null(failure);
        Assert.Single(await StoredAsync(person));
    }

    [FunctionalFact]
    public async Task GivenALiveGoogleUser_WhenATombstoneIsInserted_ThenTheDatabasePermitsIt()
    {
        // Given
        var googleUser = await SeedGoogleUserAsync();

        // When
        var failure = await RunAsync(InsertStatement(googleUser, ErasedSubjectKinds.GoogleUser));

        // Then
        Assert.Null(failure);
        Assert.Single(await StoredAsync(googleUser));
    }

    [FunctionalFact]
    public async Task GivenALiveScopesId_WhenATombstoneIsInserted_ThenTheDatabaseRefuses()
    {
        // Given — the attack R1 closes: tombstone a scope, then clear every target naming it
        var scope = await SeedScopeAsync();

        // When
        var failure = await RunAsync(InsertStatement(scope.PublicId, ErasedSubjectKinds.Person));

        // Then
        AssertRefusedByTheGuard(failure);
        Assert.Empty(await StoredAsync(scope.PublicId));
    }

    [FunctionalFact]
    public async Task GivenAnIdNobodyHolds_WhenATombstoneIsInserted_ThenTheDatabaseRefuses()
    {
        // Given — an id that is no person's now, whatever it once was
        var nobody = Guid.NewGuid();

        // When
        var failure = await RunAsync(InsertStatement(nobody, ErasedSubjectKinds.Person));

        // Then
        AssertRefusedByTheGuard(failure);
        Assert.Empty(await StoredAsync(nobody));
    }

    [FunctionalFact]
    public async Task GivenALivePersonsTombstone_WhenItIsUpsertedAgain_ThenTheDatabasePermitsIt()
    {
        // Given
        var person = await SeedPersonAsync();
        Assert.Null(await RunAsync(InsertStatement(person, ErasedSubjectKinds.Person)));

        // When — the statement ErasedSubjects issues on a retry
        var failure = await RunAsync(
            $"INSERT INTO heimdall.erased_subject (public_id, kind, created_at) VALUES ('{person}', 1, now()) " +
            "ON CONFLICT (public_id) DO UPDATE SET created_at = EXCLUDED.created_at");

        // Then
        Assert.Null(failure);
        Assert.Single(await StoredAsync(person));
    }

    [FunctionalFact]
    public async Task GivenATombstone_WhenItsIdIsRewrittenToAScopes_ThenTheDatabaseRefuses()
    {
        // Given a legitimate tombstone, and a scope whose targets somebody wants to clear
        var person = await SeedPersonAsync();
        var scope = await SeedScopeAsync();
        Assert.Null(await RunAsync(InsertStatement(person, ErasedSubjectKinds.Person)));

        // When
        var failure = await RunAsync(
            $"UPDATE heimdall.erased_subject SET public_id = '{scope.PublicId}' WHERE public_id = '{person}'");

        // Then
        AssertRefusedByTheGuard(failure);
        Assert.Single(await StoredAsync(person));
        Assert.Empty(await StoredAsync(scope.PublicId));
    }
}
