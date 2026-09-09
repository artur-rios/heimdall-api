using ArturRios.Heimdall.WebApi.Tests.Support;
using ArturRios.Util.Test.Attributes;
using Npgsql;

namespace ArturRios.Heimdall.WebApi.Tests;

/// <summary>
///     The narrowed append-only rule on <c>audit_log</c> (NFR-21), exercised from the same
///     connection the application uses.
/// </summary>
/// <remarks>
///     <para>
///         Threat Model TH-18 was closed by refusing every write to this table. Erasure needs one
///         exception, and the whole question is whether that exception is narrow enough to keep the
///         guarantee worth having. These tests are the answer, and they are functional by necessity:
///         the rule lives in a database trigger, so nothing short of the real database can show what
///         it permits.
///     </para>
///     <para>
///         What must hold: an action can never be denied, and who took it can never be reassigned —
///         only forgotten, on schedule or on erasure.
///     </para>
/// </remarks>
[Collection(nameof(FunctionalCollection))]
public class AuditActorClearingTests(PostgresFixture fixture)
{
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

    /// <summary>Inserts an entry attributed to <paramref name="actorId" />, aged as given.</summary>
    private async Task<Guid> InsertEntryAsync(Guid actorId, int ageInDays)
    {
        var publicId = Guid.NewGuid();

        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(
            """
            INSERT INTO heimdall.audit_log
                (public_id, action, succeeded, actor_person_id, actor_role, created_at)
            VALUES
                (@publicId, 'AuditActorClearingTestCommand', true, @actorId, 1,
                 now() - make_interval(days => @ageInDays))
            """,
            connection);

        command.Parameters.AddWithValue("publicId", publicId);
        command.Parameters.AddWithValue("actorId", actorId);
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
                (@publicId, 'Audit Actor', @email, '\x01', '\x02', 3, @anonymised,
                 CASE WHEN @anonymised THEN now() ELSE NULL END)
            """,
            connection);

        command.Parameters.AddWithValue("publicId", publicId);
        command.Parameters.AddWithValue("email", $"audit-actor-{Guid.NewGuid():N}@functional.test");
        command.Parameters.AddWithValue("anonymised", anonymised);

        await command.ExecuteNonQueryAsync();

        return publicId;
    }

    private static string ClearStatement(Guid publicId) =>
        $"UPDATE heimdall.audit_log SET actor_person_id = NULL, actor_role = NULL " +
        $"WHERE public_id = '{publicId}'";

    [FunctionalFact]
    public async Task GivenAnAttributionPastTheFloor_WhenCleared_ThenTheDatabasePermitsIt()
    {
        var actor = await InsertPersonAsync(anonymised: false);
        var entry = await InsertEntryAsync(actor, ageInDays: 400);

        var failure = await RunAsync(ClearStatement(entry));

        Assert.Null(failure);

        // Cleared, and the entry still says what happened
        Assert.Null(await ScalarAsync<Guid?>(
            $"SELECT actor_person_id FROM heimdall.audit_log WHERE public_id = '{entry}'"));
        Assert.Equal("AuditActorClearingTestCommand", await ScalarAsync<string>(
            $"SELECT action FROM heimdall.audit_log WHERE public_id = '{entry}'"));
    }

    [FunctionalFact]
    public async Task GivenARecentAttribution_WhenCleared_ThenTheDatabaseRefuses()
    {
        // The anti-tamper floor: somebody who has just acted must not be able to erase their own
        // attribution to cover it up.
        var actor = await InsertPersonAsync(anonymised: false);
        var entry = await InsertEntryAsync(actor, ageInDays: 1);

        var failure = await RunAsync(ClearStatement(entry));

        Assert.IsType<PostgresException>(failure);
        Assert.Equal(actor, await ScalarAsync<Guid?>(
            $"SELECT actor_person_id FROM heimdall.audit_log WHERE public_id = '{entry}'"));
    }

    [FunctionalFact]
    public async Task GivenAnErasedActor_WhenClearedRegardlessOfAge_ThenTheDatabasePermitsIt()
    {
        // A subject-requested erasure completes 30 days after the request, and the entries it
        // produced days earlier must go with it — waiting for the attribution period would leave an
        // erased person named for another seventeen months.
        var actor = await InsertPersonAsync(anonymised: true);
        var entry = await InsertEntryAsync(actor, ageInDays: 1);

        var failure = await RunAsync(ClearStatement(entry));

        Assert.Null(failure);
        Assert.Null(await ScalarAsync<Guid?>(
            $"SELECT actor_person_id FROM heimdall.audit_log WHERE public_id = '{entry}'"));
    }

    [FunctionalFact]
    public async Task GivenADueAttribution_WhenReassignedToSomebodyElse_ThenTheDatabaseRefuses()
    {
        // Making the trail say somebody else acted is worse than it not saying who did.
        var actor = await InsertPersonAsync(anonymised: false);
        var someoneElse = await InsertPersonAsync(anonymised: false);
        var entry = await InsertEntryAsync(actor, ageInDays: 400);

        var failure = await RunAsync(
            $"UPDATE heimdall.audit_log SET actor_person_id = '{someoneElse}' " +
            $"WHERE public_id = '{entry}'");

        Assert.IsType<PostgresException>(failure);
        Assert.Equal(actor, await ScalarAsync<Guid?>(
            $"SELECT actor_person_id FROM heimdall.audit_log WHERE public_id = '{entry}'"));
    }

    [FunctionalFact]
    public async Task GivenADueAttribution_WhenAnotherColumnChangesToo_ThenTheDatabaseRefuses()
    {
        // The exception is the attribution alone; it must not become a way to smuggle a rewrite.
        var actor = await InsertPersonAsync(anonymised: false);
        var entry = await InsertEntryAsync(actor, ageInDays: 400);

        var failure = await RunAsync(
            $"UPDATE heimdall.audit_log SET actor_person_id = NULL, actor_role = NULL, " +
            $"succeeded = false WHERE public_id = '{entry}'");

        Assert.IsType<PostgresException>(failure);
        Assert.True(await ScalarAsync<bool?>(
            $"SELECT succeeded FROM heimdall.audit_log WHERE public_id = '{entry}'"));
        Assert.Equal(actor, await ScalarAsync<Guid?>(
            $"SELECT actor_person_id FROM heimdall.audit_log WHERE public_id = '{entry}'"));
    }

    [FunctionalFact]
    public async Task GivenADueAttribution_WhenTheActionIsRewritten_ThenTheDatabaseRefuses()
    {
        var actor = await InsertPersonAsync(anonymised: false);
        var entry = await InsertEntryAsync(actor, ageInDays: 400);

        var failure = await RunAsync(
            $"UPDATE heimdall.audit_log SET action = 'SomethingElse' WHERE public_id = '{entry}'");

        Assert.IsType<PostgresException>(failure);
    }

    [FunctionalFact]
    public async Task GivenAnAuditEntry_WhenDeleted_ThenTheDatabaseStillRefuses()
    {
        // TH-18 unchanged: nothing is removable, however old.
        var actor = await InsertPersonAsync(anonymised: false);
        var entry = await InsertEntryAsync(actor, ageInDays: 4000);

        var failure = await RunAsync($"DELETE FROM heimdall.audit_log WHERE public_id = '{entry}'");

        Assert.IsType<PostgresException>(failure);
        Assert.Equal(1L, await ScalarAsync<long?>(
            $"SELECT count(*) FROM heimdall.audit_log WHERE public_id = '{entry}'"));
    }

    [FunctionalFact]
    public async Task GivenTheAuditTable_WhenTruncated_ThenTheDatabaseStillRefuses()
    {
        var failure = await RunAsync("TRUNCATE heimdall.audit_log");

        Assert.IsType<PostgresException>(failure);
    }
}
