using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Query.Handlers;
using ArturRios.Heimdall.Query.Input;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Heimdall.Shared.Retention;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Mock;

namespace ArturRios.Heimdall.Query.Tests;

// Unit tests for DetectSecuritySignalsQueryHandler (NFR-26). The property that matters is that a
// signal is a *rate*: twenty refusals over a year is somebody who forgets their password, twenty in
// a quarter of an hour is somebody trying things, and a detector that cannot tell them apart alerts
// on the wrong one and misses the right one.
public class DetectSecuritySignalsQueryHandlerTests
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    private static DataRetentionOptions Options(int refusals = 5, int lockouts = 3) => new()
    {
        MonitoringWindow = Window,
        RefusalThreshold = refusals,
        LockoutThreshold = lockouts
    };

    private static DetectSecuritySignalsQueryHandler Handler(
        AsyncFakeRepository<AuditLog> entries,
        AsyncFakeRepository<Person> persons,
        DataRetentionOptions? options = null) =>
        new(entries, persons, options ?? Options());

    private static async Task SeedRefusalsAsync(
        AsyncFakeRepository<AuditLog> entries, Guid actorId, int count, TimeSpan age, string? reason = null)
    {
        for (var i = 0; i < count; i++)
        {
            await entries.CreateAsync(new AuditLog
            {
                PublicId = Guid.NewGuid(),
                ActorPersonId = actorId,
                Action = "UpdateScopeCommand",
                Succeeded = false,
                FailureReason = reason ?? "Not authorized.",
                CreatedAt = DateTime.UtcNow - age
            });
        }
    }

    private static async Task SeedLockedOutAsync(AsyncFakeRepository<Person> persons, int count)
    {
        for (var i = 0; i < count; i++)
        {
            await persons.CreateAsync(new Person
            {
                PublicId = Guid.NewGuid(),
                Name = "Ada",
                Email = $"ada-{Guid.NewGuid():N}@test.local",
                RoleId = (long)Roles.User,
                LockedOutUntil = DateTime.UtcNow.AddMinutes(10)
            });
        }
    }

    [UnitFact]
    public async Task GivenSustainedRefusalsByOneActor_WhenDetecting_ThenASignalIsRaised()
    {
        var entries = new AsyncFakeRepository<AuditLog>();
        var actor = Guid.NewGuid();
        await SeedRefusalsAsync(entries, actor, count: 6, age: TimeSpan.FromMinutes(2));

        var output = await Handler(entries, new()).HandleAsync(new DetectSecuritySignalsQuery());

        var signal = Assert.Single(output.Data!.Signals);

        Assert.Equal(DetectSecuritySignalsQueryHandler.RepeatedRefusals, signal.Kind);
        Assert.Equal(actor, signal.ActorId);
        Assert.Equal(6, signal.Count);
    }

    [UnitFact]
    public async Task GivenTheSameRefusalsSpreadBeyondTheWindow_WhenDetecting_ThenNothingIsRaised()
    {
        // The distinction the whole design rests on: a total would alert here, and it should not.
        var entries = new AsyncFakeRepository<AuditLog>();
        await SeedRefusalsAsync(entries, Guid.NewGuid(), count: 20, age: TimeSpan.FromDays(30));

        var output = await Handler(entries, new()).HandleAsync(new DetectSecuritySignalsQuery());

        Assert.Empty(output.Data!.Signals);
    }

    [UnitFact]
    public async Task GivenRefusalsBelowTheThreshold_WhenDetecting_ThenNothingIsRaised()
    {
        var entries = new AsyncFakeRepository<AuditLog>();
        await SeedRefusalsAsync(entries, Guid.NewGuid(), count: 2, age: TimeSpan.FromMinutes(1));

        var output = await Handler(entries, new()).HandleAsync(new DetectSecuritySignalsQuery());

        Assert.Empty(output.Data!.Signals);
    }

    [UnitFact]
    public async Task GivenRefusalsSpreadAcrossManyActors_WhenDetecting_ThenNoOneActorTriggers()
    {
        // Grouping by actor is what makes this "somebody probing" rather than "a busy afternoon"
        var entries = new AsyncFakeRepository<AuditLog>();

        for (var i = 0; i < 10; i++)
        {
            await SeedRefusalsAsync(entries, Guid.NewGuid(), count: 2, age: TimeSpan.FromMinutes(1));
        }

        var output = await Handler(entries, new()).HandleAsync(new DetectSecuritySignalsQuery());

        Assert.Empty(output.Data!.Signals);
    }

    [UnitFact]
    public async Task GivenAnonymousRefusals_WhenDetecting_ThenTheyAreNotGrouped()
    {
        // An anonymous write has no actor to group by, and the per-IP limiter is what bounds them
        var entries = new AsyncFakeRepository<AuditLog>();

        for (var i = 0; i < 20; i++)
        {
            await entries.CreateAsync(new AuditLog
            {
                PublicId = Guid.NewGuid(),
                ActorPersonId = null,
                Action = "LoginCommand",
                Succeeded = false,
                CreatedAt = DateTime.UtcNow
            });
        }

        var output = await Handler(entries, new()).HandleAsync(new DetectSecuritySignalsQuery());

        Assert.DoesNotContain(
            output.Data!.Signals, s => s.Kind == DetectSecuritySignalsQueryHandler.RepeatedRefusals);
    }

    [UnitFact]
    public async Task GivenManyAccountsLockedOutAtOnce_WhenDetecting_ThenASignalIsRaised()
    {
        var persons = new AsyncFakeRepository<Person>();
        await SeedLockedOutAsync(persons, count: 4);

        var output = await Handler(new(), persons).HandleAsync(new DetectSecuritySignalsQuery());

        var signal = Assert.Single(output.Data!.Signals);

        Assert.Equal(DetectSecuritySignalsQueryHandler.LockoutSpike, signal.Kind);
        Assert.Equal(4, signal.Count);
        Assert.Null(signal.ActorId);
    }

    [UnitFact]
    public async Task GivenAnExpiredLockout_WhenDetecting_ThenItIsNotCounted()
    {
        // A lockout that has already elapsed is history, not a signal
        var persons = new AsyncFakeRepository<Person>();

        for (var i = 0; i < 5; i++)
        {
            await persons.CreateAsync(new Person
            {
                PublicId = Guid.NewGuid(),
                Name = "Ada",
                Email = $"ada-{Guid.NewGuid():N}@test.local",
                RoleId = (long)Roles.User,
                LockedOutUntil = DateTime.UtcNow.AddMinutes(-30)
            });
        }

        var output = await Handler(new(), persons).HandleAsync(new DetectSecuritySignalsQuery());

        Assert.Empty(output.Data!.Signals);
    }

    [UnitFact]
    public async Task GivenRepeatedShedding_WhenDetecting_ThenASignalIsRaised()
    {
        // TH-03's load condition, detectable from the trail because the gate's refusal reaches it as
        // a canonical message rather than as provider text
        var entries = new AsyncFakeRepository<AuditLog>();
        await SeedRefusalsAsync(
            entries, Guid.NewGuid(), count: 6, age: TimeSpan.FromMinutes(1),
            reason: AuthMessages.AuthenticationTemporarilyUnavailable);

        var output = await Handler(entries, new()).HandleAsync(new DetectSecuritySignalsQuery());

        Assert.Contains(
            output.Data!.Signals,
            s => s.Kind == DetectSecuritySignalsQueryHandler.CredentialVerificationShedding);
    }

    [UnitFact]
    public async Task GivenAQuietSystem_WhenDetecting_ThenNothingIsRaised()
    {
        // The state of most runs, and it must not be reported as anything
        var output = await Handler(new(), new()).HandleAsync(new DetectSecuritySignalsQuery());

        Assert.True(output.Success);
        Assert.Empty(output.Data!.Signals);
        Assert.Equal(Window, output.Data.Window);
    }
}
