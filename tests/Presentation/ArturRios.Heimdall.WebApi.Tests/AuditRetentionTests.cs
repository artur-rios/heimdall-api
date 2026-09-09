using ArturRios.Data.Relational.Core.Repositories;
using ArturRios.Heimdall.Command.Handlers;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Data.Configuration;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Shared.Retention;
using ArturRios.Heimdall.WebApi.Tests.Support;
using ArturRios.Util.Test.Attributes;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Heimdall.WebApi.Tests;

/// <summary>
///     The audit pseudonymisation pass and the database trigger together (NFR-21).
/// </summary>
/// <remarks>
///     <para>
///         The unit tests show which entries the handler selects; `AuditActorClearingTests` shows
///         what the trigger permits. Neither shows the thing that actually has to work, which is
///         that the <c>UPDATE</c> EF generates is one the trigger accepts. EF sets only the modified
///         columns, and the trigger compares every column — so this passes only if that assumption
///         holds, and it is exactly the kind of assumption a provider upgrade can break silently.
///     </para>
///     <para>
///         The pass is also the mechanism by which an erasure reaches the trail at all, so a test
///         seeds an erased identity and asserts the trail stops naming them.
///     </para>
/// </remarks>
[Collection(nameof(FunctionalCollection))]
public class AuditRetentionTests(PostgresFixture fixture)
{
    private static readonly TimeSpan AttributionPeriod = TimeSpan.FromDays(548);

    private static PseudonymiseAuditActorsCommandHandler Handler(AppDbContext context)
    {
        var entries = new EfRepository<AuditLog>(context);

        return new PseudonymiseAuditActorsCommandHandler(
            entries, entries,
            new EfRepository<Person>(context),
            new EfRepository<GoogleUser>(context),
            new DataRetentionOptions { AuditActorRetention = AttributionPeriod, PurgeBatchSize = 500 });
    }

    private async Task<Person> SeedPersonAsync(AppDbContext context, DateTime? anonymisedAt)
    {
        var person = new Person
        {
            PublicId = Guid.NewGuid(),
            Name = anonymisedAt is null ? "Ada Lovelace" : "Anonymised",
            Email = $"audit-{Guid.NewGuid():N}@functional.test",
            PasswordHash = [1],
            Salt = [2],
            RoleId = (long)Roles.User,
            IsDeleted = anonymisedAt is not null,
            AnonymisedAt = anonymisedAt
        };

        context.Persons.Add(person);
        await context.SaveChangesAsync();

        return person;
    }

    private static async Task<AuditLog> SeedEntryAsync(AppDbContext context, Guid actorId, DateTime createdAt)
    {
        var entry = new AuditLog
        {
            PublicId = Guid.NewGuid(),
            ActorPersonId = actorId,
            ActorRole = (int)Roles.User,
            Action = "AuditRetentionTestCommand",
            Succeeded = true,
            CreatedAt = createdAt
        };

        context.AuditLogs.Add(entry);
        await context.SaveChangesAsync();

        return entry;
    }

    [FunctionalFact]
    public async Task GivenAnEntryPastItsPeriod_WhenThePassRuns_ThenTheDatabaseAcceptsTheWrite()
    {
        await using var context = fixture.CreateContext();

        var actor = await SeedPersonAsync(context, anonymisedAt: null);
        var due = await SeedEntryAsync(
            context, actor.PublicId, DateTime.UtcNow - AttributionPeriod.Add(TimeSpan.FromDays(30)));
        var recent = await SeedEntryAsync(context, actor.PublicId, DateTime.UtcNow.AddDays(-1));

        var output = await Handler(context).HandleAsync(new PseudonymiseAuditActorsCommand());

        // The whole point: EF's UPDATE is one the trigger permits
        Assert.True(output.Success);
        Assert.True(output.Data!.EntriesPseudonymised >= 1);

        await using var verification = fixture.CreateContext();

        var cleared = await verification.AuditLogs.SingleAsync(x => x.Id == due.Id);

        Assert.Null(cleared.ActorPersonId);
        Assert.Null(cleared.ActorRole);

        // And the entry still says what happened — the trigger would have refused otherwise
        Assert.Equal("AuditRetentionTestCommand", cleared.Action);
        Assert.True(cleared.Succeeded);
        Assert.Equal(due.PublicId, cleared.PublicId);

        // The recent one keeps its attribution: inside the period and its actor is live
        var untouched = await verification.AuditLogs.SingleAsync(x => x.Id == recent.Id);

        Assert.Equal(actor.PublicId, untouched.ActorPersonId);
    }

    [FunctionalFact]
    public async Task GivenAnErasedIdentity_WhenThePassRuns_ThenTheTrailStopsNamingThem()
    {
        // This is the closing half of the erasure story: #92 anonymises the record, and until this
        // pass runs the audit trail still names the person who asked to be forgotten.
        await using var context = fixture.CreateContext();

        var erased = await SeedPersonAsync(context, anonymisedAt: DateTime.UtcNow.AddHours(-1));
        var entry = await SeedEntryAsync(context, erased.PublicId, DateTime.UtcNow.AddDays(-3));

        var output = await Handler(context).HandleAsync(new PseudonymiseAuditActorsCommand());

        Assert.True(output.Success);

        await using var verification = fixture.CreateContext();
        var stored = await verification.AuditLogs.SingleAsync(x => x.Id == entry.Id);

        Assert.Null(stored.ActorPersonId);

        // No audit entry anywhere still resolves to them
        Assert.False(await verification.AuditLogs.AnyAsync(x => x.ActorPersonId == erased.PublicId));
    }

    [FunctionalFact]
    public async Task GivenTheSamePassTwice_WhenItRunsAgain_ThenNothingIsRewritten()
    {
        await using var context = fixture.CreateContext();

        var actor = await SeedPersonAsync(context, anonymisedAt: null);
        var entry = await SeedEntryAsync(
            context, actor.PublicId, DateTime.UtcNow - AttributionPeriod.Add(TimeSpan.FromDays(30)));

        await Handler(context).HandleAsync(new PseudonymiseAuditActorsCommand());

        await using var second = fixture.CreateContext();
        var output = await Handler(second).HandleAsync(new PseudonymiseAuditActorsCommand());

        // Already-cleared entries are not selected, so a second run neither errors nor rewrites
        Assert.True(output.Success);

        await using var verification = fixture.CreateContext();

        Assert.Null((await verification.AuditLogs.SingleAsync(x => x.Id == entry.Id)).ActorPersonId);
    }
}
