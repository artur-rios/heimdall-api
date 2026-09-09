using ArturRios.Heimdall.Command.Handlers;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Heimdall.Shared.Retention;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Mock;

namespace ArturRios.Heimdall.Command.Tests;

// Unit tests for PseudonymiseAuditActorsCommandHandler (NFR-21): which entries a run selects.
//
// What it is permitted to *write* is not tested here and cannot be — the database trigger is the
// authority on that, and an in-memory fake enforces none of it. AuditActorClearingTests covers the
// trigger against real PostgreSQL, and AuditRetentionTests covers the two together.
public class PseudonymiseAuditActorsCommandHandlerTests
{
    private static readonly TimeSpan AttributionPeriod = TimeSpan.FromDays(548);

    private sealed record Fakes(
        AsyncFakeRepository<AuditLog> Entries,
        AsyncFakeRepository<Person> Persons,
        AsyncFakeRepository<GoogleUser> GoogleUsers)
    {
        public static Fakes New() => new(new(), new(), new());

        public PseudonymiseAuditActorsCommandHandler Handler(int batchSize = 500) =>
            new(Entries, Entries, Persons, GoogleUsers,
                new DataRetentionOptions { AuditActorRetention = AttributionPeriod, PurgeBatchSize = batchSize });
    }

    private static async Task<AuditLog> SeedEntryAsync(Fakes fakes, Guid? actorId, DateTime createdAt)
    {
        var entry = new AuditLog
        {
            PublicId = Guid.NewGuid(),
            ActorPersonId = actorId,
            ActorRole = actorId is null ? null : (int)Roles.User,
            Action = "CreateScopeCommand",
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
    }
}
