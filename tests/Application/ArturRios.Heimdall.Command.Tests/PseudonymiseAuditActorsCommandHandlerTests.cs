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
// which of each entry's actor and person target it clears — including the target of a hard-deleted
// person, known only by its tombstone (#195) — and which tombstones the run then removes.
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
        AsyncFakeRepository<GoogleUser, long> GoogleUsers,
        AsyncFakeRepository<ErasedSubject, Guid> Tombstones)
    {
        public static Fakes New() => new(new(), new(), new(), new());

        public PseudonymiseAuditActorsCommandHandler Handler(int batchSize = 500) =>
            new(Entries, Entries, Persons, GoogleUsers, Tombstones, Tombstones,
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

    /// <summary>
    ///     Tombstones <paramref name="id" /> as a hard delete would have, <paramref name="age" /> ago.
    /// </summary>
    private static async Task SeedTombstoneAsync(Fakes fakes, Guid id, TimeSpan age)
    {
        var tombstone = new ErasedSubject
        {
            Kind = (int)ErasedSubjectKinds.Person,
            CreatedAt = DateTime.UtcNow - age
        };

        // The fake assigns a fresh Guid key on create; a tombstone's key is the erased id itself.
        await fakes.Tombstones.CreateAsync(tombstone);
        tombstone.Id = id;
    }

    private static async Task<bool> TombstonedAsync(Fakes fakes, Guid id) =>
        (await fakes.Tombstones.GetAllAsync()).Data!.Any(x => x.Id == id);

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
        Assert.Equal(0, output.Data.TombstonesRemoved);
    }

    [UnitFact]
    public async Task GivenARecentEntryAboutAHardDeletedPerson_WhenPseudonymising_ThenTheTargetIsCleared()
    {
        // Given an hour-old entry by a live administrator about a person since hard-deleted: no row
        // is left, but the tombstone says the id was a person's, so it is due at any age
        var fakes = Fakes.New();
        var actor = await SeedPersonAsync(fakes, anonymisedAt: null);
        var removed = Guid.NewGuid();
        await SeedTombstoneAsync(fakes, removed, TimeSpan.FromMinutes(5));
        var entry = await SeedEntryAsync(fakes, actor.PublicId, DateTime.UtcNow.AddHours(-1), removed);

        // When
        var output = await fakes.Handler().HandleAsync(new PseudonymiseAuditActorsCommand());

        // Then — the target is gone and the actor, not due, stays
        Assert.True(output.Success);
        Assert.Equal(0, output.Data!.EntriesPseudonymised);
        Assert.Equal(1, output.Data.TargetsCleared);
        Assert.Null(entry.TargetId);
        Assert.Equal(actor.PublicId, entry.ActorPersonId);
        Assert.Equal("CreateScopeCommand", entry.Action);
    }

    [UnitFact]
    public async Task GivenATombstonedIdStillOnRecord_WhenPseudonymising_ThenARecentTargetIsKept()
    {
        // Given a person whose hard delete wrote the tombstone and then failed: the row is still
        // there, so the tombstone is no ground, and a recent entry about a live person is not due
        var fakes = Fakes.New();
        var target = await SeedPersonAsync(fakes, anonymisedAt: null);
        await SeedTombstoneAsync(fakes, target.PublicId, TimeSpan.FromDays(3));
        var entry = await SeedEntryAsync(fakes, actorId: null, DateTime.UtcNow.AddDays(-1), target.PublicId);

        // When
        var output = await fakes.Handler().HandleAsync(new PseudonymiseAuditActorsCommand());

        // Then — kept, and so is the tombstone, which the entry still names
        Assert.Equal(0, output.Data!.TargetsCleared);
        Assert.Equal(target.PublicId, entry.TargetId);
        Assert.Equal(0, output.Data.TombstonesRemoved);
        Assert.True(await TombstonedAsync(fakes, target.PublicId));
    }

    [UnitFact]
    public async Task GivenATombstonedIdStillOnRecord_WhenPseudonymising_ThenATargetDueOnTheOldGroundsIsCleared()
    {
        // Given the same failed delete, but an entry past the attribution period
        var fakes = Fakes.New();
        var target = await SeedPersonAsync(fakes, anonymisedAt: null);
        await SeedTombstoneAsync(fakes, target.PublicId, TimeSpan.FromDays(3));
        var entry = await SeedEntryAsync(fakes, actorId: null, PastThePeriod, target.PublicId);

        // When
        var output = await fakes.Handler().HandleAsync(new PseudonymiseAuditActorsCommand());

        // Then — cleared on age, exactly as without the tombstone
        Assert.Equal(1, output.Data!.TargetsCleared);
        Assert.Null(entry.TargetId);
    }

    [UnitFact]
    public async Task GivenAnOrphanedTargetWithNoTombstone_WhenPseudonymising_ThenTheTargetIsKept()
    {
        // Given ids that resolve to no row and were never tombstoned — a hard-deleted scope's, or a
        // person's removed before tombstones existed — recent and ancient
        var fakes = Fakes.New();
        var recentOrphan = Guid.NewGuid();
        var ancientOrphan = Guid.NewGuid();
        var recent = await SeedEntryAsync(fakes, actorId: null, DateTime.UtcNow.AddHours(-1), recentOrphan);
        var ancient = await SeedEntryAsync(fakes, actorId: null, DateTime.UtcNow.AddDays(-4000), ancientOrphan);

        // When
        var output = await fakes.Handler().HandleAsync(new PseudonymiseAuditActorsCommand());

        // Then — nothing says either named a person, so both stay as recorded
        Assert.Equal(0, output.Data!.TargetsCleared);
        Assert.Equal(recentOrphan, recent.TargetId);
        Assert.Equal(ancientOrphan, ancient.TargetId);
    }

    [UnitFact]
    public async Task GivenTombstones_WhenPseudonymising_ThenOnlyOldOnesNothingNamesAreRemoved()
    {
        // Given an old tombstone nothing names, a young one nothing names yet (its hard delete's own
        // entry may still be on its way), and an old one a recent entry about a live row still names
        var fakes = Fakes.New();
        var settled = Guid.NewGuid();
        var young = Guid.NewGuid();
        var stillNamed = await SeedPersonAsync(fakes, anonymisedAt: null);
        await SeedTombstoneAsync(fakes, settled, TimeSpan.FromDays(2));
        await SeedTombstoneAsync(fakes, young, TimeSpan.FromHours(1));
        await SeedTombstoneAsync(fakes, stillNamed.PublicId, TimeSpan.FromDays(2));
        await SeedEntryAsync(fakes, actorId: null, DateTime.UtcNow.AddDays(-1), stillNamed.PublicId);

        // When
        var output = await fakes.Handler().HandleAsync(new PseudonymiseAuditActorsCommand());

        // Then
        Assert.True(output.Success);
        Assert.Equal(1, output.Data!.TombstonesRemoved);
        Assert.False(await TombstonedAsync(fakes, settled));
        Assert.True(await TombstonedAsync(fakes, young));
        Assert.True(await TombstonedAsync(fakes, stillNamed.PublicId));
    }

    [UnitFact]
    public async Task GivenAnOldTombstoneWhoseLastEntryIsClearedInThePass_WhenPseudonymising_ThenItIsRemovedInTheSamePass()
    {
        // Given a person hard-deleted two days ago, still named by one entry
        var fakes = Fakes.New();
        var removed = Guid.NewGuid();
        await SeedTombstoneAsync(fakes, removed, TimeSpan.FromDays(2));
        var entry = await SeedEntryAsync(fakes, actorId: null, DateTime.UtcNow.AddDays(-2), removed);

        // When
        var output = await fakes.Handler().HandleAsync(new PseudonymiseAuditActorsCommand());

        // Then — cleared first, so by the cleanup nothing names it
        Assert.Equal(1, output.Data!.TargetsCleared);
        Assert.Equal(1, output.Data.TombstonesRemoved);
        Assert.Null(entry.TargetId);
        Assert.False(await TombstonedAsync(fakes, removed));
    }

    [UnitFact]
    public async Task GivenMoreTombstonedEntriesThanTheBatchSize_WhenPseudonymising_ThenOldestGoFirstAndTheTombstoneWaits()
    {
        // Given three entries about one hard-deleted person, an older one about another, and a
        // batch of two
        var fakes = Fakes.New();
        var removed = Guid.NewGuid();
        var otherRemoved = Guid.NewGuid();
        await SeedTombstoneAsync(fakes, removed, TimeSpan.FromDays(2));
        await SeedTombstoneAsync(fakes, otherRemoved, TimeSpan.FromDays(2));
        var oldest = await SeedEntryAsync(fakes, actorId: null, DateTime.UtcNow.AddDays(-9), otherRemoved);
        var entries = new List<AuditLog>();

        for (var i = 0; i < 3; i++)
        {
            entries.Add(await SeedEntryAsync(fakes, actorId: null, DateTime.UtcNow.AddDays(-3 + i), removed));
        }

        var handler = fakes.Handler(batchSize: 2);

        // When
        var first = await handler.HandleAsync(new PseudonymiseAuditActorsCommand());

        // Then — the two oldest are cleared; the tombstone they shared with a later entry stays
        Assert.Equal(2, first.Data!.TargetsCleared);
        Assert.Null(oldest.TargetId);
        Assert.Null(entries[0].TargetId);
        Assert.Equal(removed, entries[1].TargetId);
        Assert.Equal(1, first.Data.TombstonesRemoved);
        Assert.True(await TombstonedAsync(fakes, removed));
        Assert.False(await TombstonedAsync(fakes, otherRemoved));

        // When — the next runs finish the rest
        var second = await handler.HandleAsync(new PseudonymiseAuditActorsCommand());

        // Then
        Assert.Equal(2, second.Data!.TargetsCleared);
        Assert.Equal(1, second.Data.TombstonesRemoved);
        Assert.All(entries, entry => Assert.Null(entry.TargetId));
        Assert.Empty((await fakes.Tombstones.GetAllAsync()).Data!);
    }
}
