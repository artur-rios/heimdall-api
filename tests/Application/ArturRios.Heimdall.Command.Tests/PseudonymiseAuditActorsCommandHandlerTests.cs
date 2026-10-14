using ArturRios.Heimdall.Command.Handlers;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Heimdall.Shared.Retention;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Mock;

namespace ArturRios.Heimdall.Command.Tests;

// Unit tests for PseudonymiseAuditActorsCommandHandler (NFR-21): which entries a run selects, and
// which of each entry's actor and person target it clears.
//
// What it is permitted to *write* is not tested here and cannot be — the database trigger is the
// authority on that, and an in-memory fake enforces none of it. AuditActorClearingTests and
// AuditTargetClearingTests cover the trigger against real PostgreSQL, and AuditRetentionTests covers
// the two together.
public class PseudonymiseAuditActorsCommandHandlerTests
{
    private static readonly TimeSpan AttributionPeriod = TimeSpan.FromDays(548);

    private sealed record Fakes(
        AsyncFakeRepository<AuditLog, long> Entries,
        AsyncFakeRepository<Person, long> Persons,
        AsyncFakeRepository<GoogleUser, long> GoogleUsers)
    {
        public static Fakes New() => new(new(), new(), new());

        public PseudonymiseAuditActorsCommandHandler Handler(int batchSize = 500) =>
            new(Entries, Entries, Persons, GoogleUsers,
                new DataRetentionOptions { AuditActorRetention = AttributionPeriod, PurgeBatchSize = batchSize });
    }

    private static async Task<AuditLog> SeedEntryAsync(
        Fakes fakes, Guid? actorId, DateTime createdAt, Guid? targetId = null)
    {
        var entry = new AuditLog
        {
            PublicId = Guid.NewGuid(),
            ActorPersonId = actorId,
            ActorRole = actorId is null ? null : (int)Roles.User,
            Action = "CreateScopeCommand",
            TargetId = targetId,
            Succeeded = true,
            CreatedAt = createdAt
        };

        await fakes.Entries.CreateAsync(entry);

        return entry;
    }

    private static async Task<Person> SeedPersonAsync(Fakes fakes, DateTime? anonymisedAt)
    {
        var person = new Person
        {
            PublicId = Guid.NewGuid(),
            Name = "Ada",
            Email = $"ada-{Guid.NewGuid():N}@test.local",
            AnonymisedAt = anonymisedAt
        };

        await fakes.Persons.CreateAsync(person);

        return person;
    }

    private static async Task<GoogleUser> SeedGoogleUserAsync(Fakes fakes, DateTime? anonymisedAt)
    {
        var googleUser = new GoogleUser
        {
            PublicId = Guid.NewGuid(),
            GoogleId = Guid.NewGuid().ToString("N"),
            Name = "Ada",
            Email = $"ada-{Guid.NewGuid():N}@test.local",
            AnonymisedAt = anonymisedAt
        };

        await fakes.GoogleUsers.CreateAsync(googleUser);

        return googleUser;
    }

    private static DateTime PastThePeriod => DateTime.UtcNow - AttributionPeriod.Add(TimeSpan.FromDays(1));

    [UnitFact]
    public async Task GivenAnEntryPastItsAttributionPeriod_WhenPseudonymising_ThenTheActorIsCleared()
    {
        var fakes = Fakes.New();
        var actor = await SeedPersonAsync(fakes, anonymisedAt: null);
        var entry = await SeedEntryAsync(fakes, actor.PublicId, DateTime.UtcNow - AttributionPeriod.Add(TimeSpan.FromDays(1)));

        var output = await fakes.Handler().HandleAsync(new PseudonymiseAuditActorsCommand());

        Assert.True(output.Success);
        Assert.Equal(RetentionMessages.AuditActorsPseudonymised, output.Messages.First());
        Assert.Equal(1, output.Data!.EntriesPseudonymised);
        Assert.Null(entry.ActorPersonId);
        Assert.Null(entry.ActorRole);

        // What happened survives; only who did it is gone
        Assert.Equal("CreateScopeCommand", entry.Action);
        Assert.True(entry.Succeeded);
    }

    [UnitFact]
    public async Task GivenAnEntryInsideItsAttributionPeriod_WhenPseudonymising_ThenItIsKept()
    {
        var fakes = Fakes.New();
        var actor = await SeedPersonAsync(fakes, anonymisedAt: null);
        var entry = await SeedEntryAsync(fakes, actor.PublicId, DateTime.UtcNow.AddDays(-100));

        var output = await fakes.Handler().HandleAsync(new PseudonymiseAuditActorsCommand());

        Assert.Equal(0, output.Data!.EntriesPseudonymised);
        Assert.Equal(actor.PublicId, entry.ActorPersonId);
    }

    [UnitFact]
    public async Task GivenAnErasedActor_WhenPseudonymising_ThenRecentEntriesAreClearedToo()
    {
        // Waiting for the attribution period would leave an erased person named in the trail for
        // another seventeen months, which is what the erasure was meant to end.
        var fakes = Fakes.New();
        var actor = await SeedPersonAsync(fakes, anonymisedAt: DateTime.UtcNow.AddDays(-1));
        var entry = await SeedEntryAsync(fakes, actor.PublicId, DateTime.UtcNow.AddDays(-2));

        var output = await fakes.Handler().HandleAsync(new PseudonymiseAuditActorsCommand());

        Assert.Equal(1, output.Data!.EntriesPseudonymised);
        Assert.Null(entry.ActorPersonId);
    }

    [UnitFact]
    public async Task GivenAnErasedGoogleUser_WhenPseudonymising_ThenTheirEntriesAreClearedToo()
    {
        var fakes = Fakes.New();
        var googleUser = new GoogleUser
        {
            PublicId = Guid.NewGuid(),
            GoogleId = "108124442542040415000",
            Name = "Ada",
            Email = "ada@test.local",
            AnonymisedAt = DateTime.UtcNow.AddDays(-1)
        };
        await fakes.GoogleUsers.CreateAsync(googleUser);
        var entry = await SeedEntryAsync(fakes, googleUser.PublicId, DateTime.UtcNow.AddDays(-2));

        var output = await fakes.Handler().HandleAsync(new PseudonymiseAuditActorsCommand());

        Assert.Equal(1, output.Data!.EntriesPseudonymised);
        Assert.Null(entry.ActorPersonId);
    }

    [UnitFact]
    public async Task GivenAnAlreadyAnonymousEntry_WhenPseudonymising_ThenItIsNotSelected()
    {
        // An anonymous write has no attribution to clear, and selecting it would have the run
        // rewrite rows for no reason.
        var fakes = Fakes.New();
        await SeedEntryAsync(fakes, actorId: null, DateTime.UtcNow.AddDays(-4000));

        var output = await fakes.Handler().HandleAsync(new PseudonymiseAuditActorsCommand());

        Assert.Equal(0, output.Data!.EntriesPseudonymised);
    }

    [UnitFact]
    public async Task GivenAnEntryPastItsPeriodAboutAPerson_WhenPseudonymising_ThenTheTargetIsCleared()
    {
        // Given an old entry about a live person, whose actor was cleared earlier — it must still be
        // selected, or existing rows would never be cleaned
        var fakes = Fakes.New();
        var target = await SeedPersonAsync(fakes, anonymisedAt: null);
        var entry = await SeedEntryAsync(fakes, actorId: null, PastThePeriod, target.PublicId);

        // When
        var output = await fakes.Handler().HandleAsync(new PseudonymiseAuditActorsCommand());

        // Then
        Assert.True(output.Success);
        Assert.Equal(0, output.Data!.EntriesPseudonymised);
        Assert.Equal(1, output.Data.TargetsCleared);
        Assert.Null(entry.TargetId);
        Assert.Equal("CreateScopeCommand", entry.Action);
    }

    [UnitFact]
    public async Task GivenAnEntryPastItsPeriodAboutAGoogleUser_WhenPseudonymising_ThenTheTargetIsCleared()
    {
        // Given
        var fakes = Fakes.New();
        var target = await SeedGoogleUserAsync(fakes, anonymisedAt: null);
        var entry = await SeedEntryAsync(fakes, actorId: null, PastThePeriod, target.PublicId);

        // When
        var output = await fakes.Handler().HandleAsync(new PseudonymiseAuditActorsCommand());

        // Then
        Assert.Equal(1, output.Data!.TargetsCleared);
        Assert.Null(entry.TargetId);
    }

    [UnitFact]
    public async Task GivenARecentEntryAboutAnErasedPerson_WhenPseudonymising_ThenOnlyTheTargetIsCleared()
    {
        // Given a day-old entry by a live administrator about somebody since erased: the target is
        // due on erasure at any age, the actor is not due at all
        var fakes = Fakes.New();
        var actor = await SeedPersonAsync(fakes, anonymisedAt: null);
        var target = await SeedPersonAsync(fakes, anonymisedAt: DateTime.UtcNow.AddHours(-1));
        var entry = await SeedEntryAsync(fakes, actor.PublicId, DateTime.UtcNow.AddDays(-1), target.PublicId);

        // When
        var output = await fakes.Handler().HandleAsync(new PseudonymiseAuditActorsCommand());

        // Then
        Assert.Equal(0, output.Data!.EntriesPseudonymised);
        Assert.Equal(1, output.Data.TargetsCleared);
        Assert.Null(entry.TargetId);
        Assert.Equal(actor.PublicId, entry.ActorPersonId);
        Assert.Equal((int)Roles.User, entry.ActorRole);
    }

    [UnitFact]
    public async Task GivenARecentEntryAboutALivePerson_WhenPseudonymising_ThenTheTargetIsKept()
    {
        // Given
        var fakes = Fakes.New();
        var target = await SeedPersonAsync(fakes, anonymisedAt: null);
        var entry = await SeedEntryAsync(fakes, actorId: null, DateTime.UtcNow.AddDays(-100), target.PublicId);

        // When
        var output = await fakes.Handler().HandleAsync(new PseudonymiseAuditActorsCommand());

        // Then
        Assert.Equal(0, output.Data!.TargetsCleared);
        Assert.Equal(target.PublicId, entry.TargetId);
    }

    [UnitFact]
    public async Task GivenAnEntryPastItsPeriodAboutAScope_WhenPseudonymising_ThenTheTargetIsKept()
    {
        // Given — a scope's id names no person, so it is not personal data and stays, however old.
        // The same holds for an id nobody has any more, as a hard-deleted person's.
        var fakes = Fakes.New();
        var scopeId = Guid.NewGuid();
        var entry = await SeedEntryAsync(fakes, actorId: null, DateTime.UtcNow.AddDays(-4000), scopeId);

        // When
        var output = await fakes.Handler().HandleAsync(new PseudonymiseAuditActorsCommand());

        // Then
        Assert.Equal(0, output.Data!.EntriesPseudonymised);
        Assert.Equal(0, output.Data.TargetsCleared);
        Assert.Equal(scopeId, entry.TargetId);
    }

    [UnitFact]
    public async Task GivenAnEntryPastItsPeriodByAndAboutPeople_WhenPseudonymising_ThenBothAreClearedInOnePass()
    {
        // Given
        var fakes = Fakes.New();
        var actor = await SeedPersonAsync(fakes, anonymisedAt: null);
        var target = await SeedPersonAsync(fakes, anonymisedAt: null);
        var entry = await SeedEntryAsync(fakes, actor.PublicId, PastThePeriod, target.PublicId);

        // When
        var output = await fakes.Handler().HandleAsync(new PseudonymiseAuditActorsCommand());

        // Then
        Assert.Equal(1, output.Data!.EntriesPseudonymised);
        Assert.Equal(1, output.Data.TargetsCleared);
        Assert.Null(entry.ActorPersonId);
        Assert.Null(entry.ActorRole);
        Assert.Null(entry.TargetId);
    }

    [UnitFact]
    public async Task GivenADueActorAndATargetNamingNoPerson_WhenPseudonymising_ThenOnlyTheActorIsCleared()
    {
        // Given
        var fakes = Fakes.New();
        var actor = await SeedPersonAsync(fakes, anonymisedAt: null);
        var scopeId = Guid.NewGuid();
        var entry = await SeedEntryAsync(fakes, actor.PublicId, PastThePeriod, scopeId);

        // When
        var output = await fakes.Handler().HandleAsync(new PseudonymiseAuditActorsCommand());

        // Then
        Assert.Equal(1, output.Data!.EntriesPseudonymised);
        Assert.Equal(0, output.Data.TargetsCleared);
        Assert.Null(entry.ActorPersonId);
        Assert.Equal(scopeId, entry.TargetId);
    }

    [UnitFact]
    public async Task GivenAnErasedActorOnARecentEntryAboutALivePerson_WhenPseudonymising_ThenOnlyTheActorIsCleared()
    {
        // Given — the actor is due on erasure; the target is a person, but young and live
        var fakes = Fakes.New();
        var actor = await SeedPersonAsync(fakes, anonymisedAt: DateTime.UtcNow.AddHours(-1));
        var target = await SeedPersonAsync(fakes, anonymisedAt: null);
        var entry = await SeedEntryAsync(fakes, actor.PublicId, DateTime.UtcNow.AddDays(-2), target.PublicId);

        // When
        var output = await fakes.Handler().HandleAsync(new PseudonymiseAuditActorsCommand());

        // Then
        Assert.Equal(1, output.Data!.EntriesPseudonymised);
        Assert.Equal(0, output.Data.TargetsCleared);
        Assert.Null(entry.ActorPersonId);
        Assert.Equal(target.PublicId, entry.TargetId);
    }

    [UnitFact]
    public async Task GivenAClearedTargetPastItsPeriod_WhenPseudonymisingAgain_ThenItIsNotSelected()
    {
        // Given
        var fakes = Fakes.New();
        var target = await SeedPersonAsync(fakes, anonymisedAt: null);
        await SeedEntryAsync(fakes, actorId: null, PastThePeriod, target.PublicId);
        await fakes.Handler().HandleAsync(new PseudonymiseAuditActorsCommand());

        // When
        var output = await fakes.Handler().HandleAsync(new PseudonymiseAuditActorsCommand());

        // Then
        Assert.Equal(0, output.Data!.EntriesPseudonymised);
        Assert.Equal(0, output.Data.TargetsCleared);
    }

    [UnitFact]
    public async Task GivenMoreEntriesThanTheBatchSize_WhenPseudonymising_ThenTheRunIsBounded()
    {
        var fakes = Fakes.New();
        var actor = await SeedPersonAsync(fakes, anonymisedAt: null);

        for (var i = 0; i < 5; i++)
        {
            await SeedEntryAsync(
                fakes, actor.PublicId, DateTime.UtcNow - AttributionPeriod.Add(TimeSpan.FromDays(10 + i)));
        }

        var handler = fakes.Handler(batchSize: 2);

        Assert.Equal(2, (await handler.HandleAsync(new PseudonymiseAuditActorsCommand())).Data!.EntriesPseudonymised);
        await handler.HandleAsync(new PseudonymiseAuditActorsCommand());
        Assert.Equal(1, (await handler.HandleAsync(new PseudonymiseAuditActorsCommand())).Data!.EntriesPseudonymised);

        Assert.All((await fakes.Entries.GetAllAsync()).Data!, entry => Assert.Null(entry.ActorPersonId));
    }

    [UnitFact]
    public async Task GivenNothingDue_WhenPseudonymising_ThenTheRunSucceedsWithNoCounts()
    {
        var fakes = Fakes.New();

        var output = await fakes.Handler().HandleAsync(new PseudonymiseAuditActorsCommand());

        Assert.True(output.Success);
        Assert.Empty(output.Errors);
        Assert.Equal(0, output.Data!.EntriesPseudonymised);
        Assert.Equal(0, output.Data.TargetsCleared);
    }
}
