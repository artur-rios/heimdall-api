using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.WebApi.Tests.Support;
using ArturRios.Util.Test.Attributes;
using Npgsql;

namespace ArturRios.Heimdall.WebApi.Tests;

/// <summary>
///     The second permitted change to <c>audit_log</c> (NFR-21, #184): a target naming a person or
///     Google User may be cleared once due, exercised from the same connection the application uses.
/// </summary>
/// <remarks>
///     <para>
///         An entry about a person stays personal data while its <c>target_id</c> still resolves to
///         them, whoever the actor was, so clearing the actor alone did not make it anonymous. The
///         trigger now allows the target to be forgotten too, on the same two grounds and the same
///         thirty-day floor as the actor, and nothing else besides.
///     </para>
///     <para>
///         What must hold: a target that names a person can be forgotten, never reassigned and never
///         early; a target that names anything else is not personal data and can never be touched;
///         and what happened stays exactly as recorded. Each case is proved against the real trigger,
///         and each refusal is checked for its own reason, so a test cannot pass on the wrong one.
///     </para>
/// </remarks>
[Collection(nameof(FunctionalCollection))]
public class AuditTargetClearingTests(PostgresFixture fixture)
{
    private const string RestrictViolation = "23001";

    private async Task<Exception?> RunAsync(string statement)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(statement, connection);

        return await Record.ExceptionAsync(() => command.ExecuteNonQueryAsync());
    }

    private async Task<T?> ScalarAsync<T>(string statement)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(statement, connection);

        var value = await command.ExecuteScalarAsync();

        return value is null or DBNull ? default : (T)value;
    }

    private Task<Guid?> TargetOfAsync(Guid entry) =>
        ScalarAsync<Guid?>($"SELECT target_id FROM heimdall.audit_log WHERE public_id = '{entry}'");

    private Task<Guid?> ActorOfAsync(Guid entry) =>
        ScalarAsync<Guid?>($"SELECT actor_person_id FROM heimdall.audit_log WHERE public_id = '{entry}'");

    /// <summary>Asserts the trigger refused the statement, for the reason given.</summary>
    private static void AssertRefused(Exception? failure, string reason)
    {
        var refusal = Assert.IsType<PostgresException>(failure);

        Assert.Equal(RestrictViolation, refusal.SqlState);
        Assert.Contains(reason, refusal.MessageText);
    }

    /// <summary>Inserts an entry with the given actor and target, aged as given.</summary>
    private async Task<Guid> InsertEntryAsync(Guid? actorId, Guid? targetId, int ageInDays)
    {
        var publicId = Guid.NewGuid();

        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(
            """
            INSERT INTO heimdall.audit_log
                (public_id, action, succeeded, actor_person_id, actor_role, target_id, created_at)
            VALUES
                (@publicId, 'AuditTargetClearingTestCommand', true, @actorId,
                 CASE WHEN @actorId IS NULL THEN NULL ELSE 1 END, @targetId,
                 now() - make_interval(days => @ageInDays))
            """,
            connection);

        command.Parameters.AddWithValue("publicId", publicId);
        command.Parameters.Add(new NpgsqlParameter<Guid?>("actorId", actorId));
        command.Parameters.Add(new NpgsqlParameter<Guid?>("targetId", targetId));
        command.Parameters.AddWithValue("ageInDays", ageInDays);

        await command.ExecuteNonQueryAsync();

        return publicId;
    }

    /// <summary>Inserts a person, optionally already anonymised, and returns their PublicId.</summary>
    private async Task<Guid> InsertPersonAsync(bool anonymised)
    {
        var publicId = Guid.NewGuid();

        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(
            """
            INSERT INTO heimdall.person
                (public_id, name, email, password_hash, salt, role_id, is_deleted, anonymised_at)
            VALUES
                (@publicId, 'Audit Target', @email, '\x01', '\x02', 3, @anonymised,
                 CASE WHEN @anonymised THEN now() ELSE NULL END)
            """,
            connection);

        command.Parameters.AddWithValue("publicId", publicId);
        command.Parameters.AddWithValue("email", $"audit-target-{Guid.NewGuid():N}@functional.test");
        command.Parameters.AddWithValue("anonymised", anonymised);

        await command.ExecuteNonQueryAsync();

        return publicId;
    }

    private async Task<Scope> InsertScopeAsync()
    {
        await using var context = fixture.CreateContext();
        var scope = new Scope { PublicId = Guid.NewGuid(), Name = $"audit-target-{Guid.NewGuid():N}" };
        context.Scopes.Add(scope);
        await context.SaveChangesAsync();

        return scope;
    }

    private async Task<Guid> InsertGoogleUserAsync(bool anonymised)
    {
        var scope = await InsertScopeAsync();

        await using var context = fixture.CreateContext();
        var googleUser = new GoogleUser
        {
            PublicId = Guid.NewGuid(),
            GoogleId = Guid.NewGuid().ToString("N"),
            Name = "Audit Target",
            Email = $"audit-target-{Guid.NewGuid():N}@functional.test",
            ScopeId = scope.Id,
            IsDeleted = anonymised,
            AnonymisedAt = anonymised ? DateTime.UtcNow : null
        };
        context.GoogleUsers.Add(googleUser);
        await context.SaveChangesAsync();

        return googleUser.PublicId;
    }

    private static string ClearTargetStatement(Guid entry) =>
        $"UPDATE heimdall.audit_log SET target_id = NULL WHERE public_id = '{entry}'";

    [FunctionalFact]
    public async Task GivenAnOldEntryTargetingAPerson_WhenTheTargetIsCleared_ThenTheDatabasePermitsIt()
    {
        // Given an entry past the floor about a live person, whose actor was cleared earlier
        var target = await InsertPersonAsync(anonymised: false);
        var entry = await InsertEntryAsync(actorId: null, target, ageInDays: 400);

        // When
        var failure = await RunAsync(ClearTargetStatement(entry));

        // Then cleared, and the entry still says what happened
        Assert.Null(failure);
        Assert.Null(await TargetOfAsync(entry));
        Assert.Equal("AuditTargetClearingTestCommand", await ScalarAsync<string>(
            $"SELECT action FROM heimdall.audit_log WHERE public_id = '{entry}'"));
    }

    [FunctionalFact]
    public async Task GivenAnOldEntryTargetingAGoogleUser_WhenTheTargetIsCleared_ThenTheDatabasePermitsIt()
    {
        // Given
        var target = await InsertGoogleUserAsync(anonymised: false);
        var entry = await InsertEntryAsync(actorId: null, target, ageInDays: 400);

        // When
        var failure = await RunAsync(ClearTargetStatement(entry));

        // Then
        Assert.Null(failure);
        Assert.Null(await TargetOfAsync(entry));
    }

    [FunctionalFact]
    public async Task GivenARecentEntryTargetingAnErasedGoogleUser_WhenTheTargetIsCleared_ThenTheDatabasePermitsIt()
    {
        // Given
        var target = await InsertGoogleUserAsync(anonymised: true);
        var entry = await InsertEntryAsync(actorId: null, target, ageInDays: 1);

        // When
        var failure = await RunAsync(ClearTargetStatement(entry));

        // Then
        Assert.Null(failure);
        Assert.Null(await TargetOfAsync(entry));
    }

    [FunctionalFact]
    public async Task GivenARecentEntryTargetingALiveGoogleUser_WhenTheTargetIsCleared_ThenTheDatabaseRefuses()
    {
        // Given
        var target = await InsertGoogleUserAsync(anonymised: false);
        var entry = await InsertEntryAsync(actorId: null, target, ageInDays: 1);

        // When
        var failure = await RunAsync(ClearTargetStatement(entry));

        // Then
        AssertRefused(failure, "target is not yet due to be cleared");
        Assert.Equal(target, await TargetOfAsync(entry));
    }

    [FunctionalFact]
    public async Task GivenARecentEntryTargetingAnErasedPerson_WhenOnlyTheTargetIsCleared_ThenTheDatabasePermitsIt()
    {
        // Given a day-old entry by a live administrator about a person since erased. The target is
        // due on erasure, at any age; the actor is not due and is left as it is.
        var actor = await InsertPersonAsync(anonymised: false);
        var target = await InsertPersonAsync(anonymised: true);
        var entry = await InsertEntryAsync(actor, target, ageInDays: 1);

        // When
        var failure = await RunAsync(ClearTargetStatement(entry));

        // Then the target is gone and the actor, written back unchanged, is still there
        Assert.Null(failure);
        Assert.Null(await TargetOfAsync(entry));
        Assert.Equal(actor, await ActorOfAsync(entry));
    }

    [FunctionalFact]
    public async Task GivenARecentEntryTargetingALivePerson_WhenTheTargetIsCleared_ThenTheDatabaseRefuses()
    {
        // Given — the floor: whoever has just acted on somebody must not be able to erase the record
        // of who that was
        var target = await InsertPersonAsync(anonymised: false);
        var entry = await InsertEntryAsync(actorId: null, target, ageInDays: 1);

        // When
        var failure = await RunAsync(ClearTargetStatement(entry));

        // Then
        AssertRefused(failure, "target is not yet due to be cleared");
        Assert.Equal(target, await TargetOfAsync(entry));
    }

    [FunctionalFact]
    public async Task GivenAnOldEntryTargetingAScope_WhenTheTargetIsCleared_ThenTheDatabaseRefuses()
    {
        // Given — a scope is not a person, so its id is not personal data and stays, however old
        var scope = await InsertScopeAsync();
        var entry = await InsertEntryAsync(actorId: null, scope.PublicId, ageInDays: 4000);

        // When
        var failure = await RunAsync(ClearTargetStatement(entry));

        // Then
        AssertRefused(failure, "target names no person");
        Assert.Equal(scope.PublicId, await TargetOfAsync(entry));
    }

    [FunctionalFact]
    public async Task GivenAnOldEntryTargetingNobodyStillOnRecord_WhenTheTargetIsCleared_ThenTheDatabaseRefuses()
    {
        // Given a target that resolves to no row at all, as a hard-deleted person's no longer does
        var vanished = Guid.NewGuid();
        var entry = await InsertEntryAsync(actorId: null, vanished, ageInDays: 4000);

        // When
        var failure = await RunAsync(ClearTargetStatement(entry));

        // Then
        AssertRefused(failure, "target names no person");
        Assert.Equal(vanished, await TargetOfAsync(entry));
    }

    [FunctionalFact]
    public async Task GivenADuePersonTarget_WhenReassignedToSomebodyElse_ThenTheDatabaseRefuses()
    {
        // Given — making the trail say an action was taken on somebody else is worse than it not
        // saying on whom
        var target = await InsertPersonAsync(anonymised: true);
        var someoneElse = await InsertPersonAsync(anonymised: false);
        var entry = await InsertEntryAsync(actorId: null, target, ageInDays: 400);

        // When
        var failure = await RunAsync(
            $"UPDATE heimdall.audit_log SET target_id = '{someoneElse}' WHERE public_id = '{entry}'");

        // Then
        AssertRefused(failure, "target may be cleared but not reassigned");
        Assert.Equal(target, await TargetOfAsync(entry));
    }

    [FunctionalFact]
    public async Task GivenAnEntryWithNoTarget_WhenATargetIsSet_ThenTheDatabaseRefuses()
    {
        // Given
        var somebody = await InsertPersonAsync(anonymised: false);
        var entry = await InsertEntryAsync(actorId: null, targetId: null, ageInDays: 400);

        // When
        var failure = await RunAsync(
            $"UPDATE heimdall.audit_log SET target_id = '{somebody}' WHERE public_id = '{entry}'");

        // Then
        AssertRefused(failure, "target may be cleared but not reassigned");
        Assert.Null(await TargetOfAsync(entry));
    }

    [FunctionalFact]
    public async Task GivenADuePersonTarget_WhenTheActionIsRewrittenAlongside_ThenTheDatabaseRefuses()
    {
        // Given — the exception is the target alone; it must not become a way to smuggle a rewrite
        var target = await InsertPersonAsync(anonymised: false);
        var entry = await InsertEntryAsync(actorId: null, target, ageInDays: 400);

        // When
        var failure = await RunAsync(
            $"UPDATE heimdall.audit_log SET target_id = NULL, action = 'SomethingElse' " +
            $"WHERE public_id = '{entry}'");

        // Then
        AssertRefused(failure, "append-only");
        Assert.Equal(target, await TargetOfAsync(entry));
    }

    [FunctionalFact]
    public async Task GivenARecentPersonTarget_WhenBackdatedToClearIt_ThenTheDatabaseRefuses()
    {
        // Given — moving created_at past the floor would otherwise make any target due at once
        var target = await InsertPersonAsync(anonymised: false);
        var entry = await InsertEntryAsync(actorId: null, target, ageInDays: 1);

        // When
        var failure = await RunAsync(
            $"UPDATE heimdall.audit_log SET target_id = NULL, created_at = created_at - interval '400 days' " +
            $"WHERE public_id = '{entry}'");

        // Then
        AssertRefused(failure, "append-only");
        Assert.Equal(target, await TargetOfAsync(entry));
    }

    [FunctionalFact]
    public async Task GivenAnEntry_WhenItsIdIsChanged_ThenTheDatabaseRefuses()
    {
        // Given an entry with nothing left to clear — the rows where no other rule applies
        var entry = await InsertEntryAsync(actorId: null, targetId: null, ageInDays: 400);
        var id = await ScalarAsync<long>($"SELECT id FROM heimdall.audit_log WHERE public_id = '{entry}'");

        // When
        var failure = await RunAsync(
            $"UPDATE heimdall.audit_log SET id = id + 1000000000 WHERE public_id = '{entry}'");

        // Then
        AssertRefused(failure, "append-only");
        Assert.Equal(id, await ScalarAsync<long>(
            $"SELECT id FROM heimdall.audit_log WHERE public_id = '{entry}'"));
    }

    [FunctionalFact]
    public async Task GivenAnOldEntryAboutAPerson_WhenActorAndTargetAreClearedTogether_ThenTheDatabasePermitsIt()
    {
        // Given
        var actor = await InsertPersonAsync(anonymised: false);
        var target = await InsertPersonAsync(anonymised: false);
        var entry = await InsertEntryAsync(actor, target, ageInDays: 400);

        // When
        var failure = await RunAsync(
            $"UPDATE heimdall.audit_log SET actor_person_id = NULL, actor_role = NULL, target_id = NULL " +
            $"WHERE public_id = '{entry}'");

        // Then
        Assert.Null(failure);
        Assert.Null(await ActorOfAsync(entry));
        Assert.Null(await TargetOfAsync(entry));
    }

    [FunctionalFact]
    public async Task GivenADueTargetAndAnUndueActor_WhenBothAreCleared_ThenTheDatabaseRefusesBoth()
    {
        // Given a recent entry by a live actor about an erased person: the target is due, the actor
        // is not, and a due target must not carry an undue actor clearing through with it
        var actor = await InsertPersonAsync(anonymised: false);
        var target = await InsertPersonAsync(anonymised: true);
        var entry = await InsertEntryAsync(actor, target, ageInDays: 1);

        // When
        var failure = await RunAsync(
            $"UPDATE heimdall.audit_log SET actor_person_id = NULL, actor_role = NULL, target_id = NULL " +
            $"WHERE public_id = '{entry}'");

        // Then
        AssertRefused(failure, "actor attribution is not yet due to be cleared");
        Assert.Equal(actor, await ActorOfAsync(entry));
        Assert.Equal(target, await TargetOfAsync(entry));
    }

    [FunctionalFact]
    public async Task GivenADueActorAndAnUndueTarget_WhenOnlyTheActorIsCleared_ThenTheDatabasePermitsIt()
    {
        // Given an old entry about a scope: the actor is due, the target is not personal data
        var actor = await InsertPersonAsync(anonymised: false);
        var scope = await InsertScopeAsync();
        var entry = await InsertEntryAsync(actor, scope.PublicId, ageInDays: 400);

        // When
        var failure = await RunAsync(
            $"UPDATE heimdall.audit_log SET actor_person_id = NULL, actor_role = NULL " +
            $"WHERE public_id = '{entry}'");

        // Then the target, written back unchanged, survives
        Assert.Null(failure);
        Assert.Null(await ActorOfAsync(entry));
        Assert.Equal(scope.PublicId, await TargetOfAsync(entry));
    }

    [FunctionalFact]
    public async Task GivenAnAlreadyClearedTarget_WhenClearedAgain_ThenTheDatabasePermitsTheNoOp()
    {
        // Given — a re-run of the pass must not be an error
        var target = await InsertPersonAsync(anonymised: false);
        var entry = await InsertEntryAsync(actorId: null, target, ageInDays: 400);
        Assert.Null(await RunAsync(ClearTargetStatement(entry)));

        // When
        var failure = await RunAsync(ClearTargetStatement(entry));

        // Then
        Assert.Null(failure);
        Assert.Null(await TargetOfAsync(entry));
    }
}
