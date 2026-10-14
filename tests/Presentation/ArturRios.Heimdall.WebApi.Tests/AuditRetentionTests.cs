using System.Net;
using ArturRios.Configuration.Enums;
using ArturRios.Data.Relational.Core.Repositories;
using ArturRios.Heimdall.Command.Handlers;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Data.Configuration;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Query.Output;
using ArturRios.Heimdall.Shared.Retention;
using ArturRios.Heimdall.WebApi.Tests.Support;
using ArturRios.Output;
using ArturRios.Util.Http;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Functional;
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
///     <para>
///         The same holds for person targets (#184): each part of an entry is cleared only when it
///         is due, so EF writes the other part back unchanged, and the trigger must accept that.
///         The last test reads the result where a System Admin would, through UC-48's listing.
///     </para>
/// </remarks>
[Collection(nameof(FunctionalCollection))]
public class AuditRetentionTests(PostgresFixture fixture) : WebApiTest<Program>(EnvironmentType.Local)
{
    private static readonly TimeSpan AttributionPeriod = TimeSpan.FromDays(548);

    private static PseudonymiseAuditActorsCommandHandler Handler(AppDbContext context)
    {
        var entries = new EfRepository<AuditLog, long>(context);

        return new PseudonymiseAuditActorsCommandHandler(
            entries, entries,
            new EfRepository<Person, long>(context),
            new EfRepository<GoogleUser, long>(context),
            new DataRetentionOptions { AuditActorRetention = AttributionPeriod, PurgeBatchSize = 500 });
    }

    private Task<HttpOutput<PaginatedOutput<AuditLogEntryOutput>?>> ListAsync(string filters) =>
        Gateway.GetAsync<PaginatedOutput<AuditLogEntryOutput>>($"/api/audit-log?pageNumber=1&pageSize=20&{filters}");

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

    private static async Task<AuditLog> SeedEntryAsync(
        AppDbContext context, Guid? actorId, DateTime createdAt, Guid? targetId = null,
        string action = "AuditRetentionTestCommand")
    {
        var entry = new AuditLog
        {
            PublicId = Guid.NewGuid(),
            ActorPersonId = actorId,
            ActorRole = actorId is null ? null : (int)Roles.User,
            Action = action,
            TargetId = targetId,
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

    [FunctionalFact]
    public async Task GivenEntriesDueInOnePartOnly_WhenThePassRuns_ThenTheDatabaseAcceptsEachWrite()
    {
        // Given an old entry about a scope, whose actor is due and whose target names no person,
        // and a recent entry by a live actor about an erased person, whose target is due and whose
        // actor is not
        await using var context = fixture.CreateContext();

        var actor = await SeedPersonAsync(context, anonymisedAt: null);
        var erased = await SeedPersonAsync(context, anonymisedAt: DateTime.UtcNow.AddHours(-1));
        var scopeId = Guid.NewGuid();
        var aboutScope = await SeedEntryAsync(
            context, actor.PublicId, DateTime.UtcNow - AttributionPeriod.Add(TimeSpan.FromDays(30)), scopeId);
        var aboutErased = await SeedEntryAsync(
            context, actor.PublicId, DateTime.UtcNow.AddDays(-1), erased.PublicId);

        // When
        var output = await Handler(context).HandleAsync(new PseudonymiseAuditActorsCommand());

        // Then each UPDATE, with its other part written back unchanged, is one the trigger permits
        Assert.True(output.Success);
        Assert.True(output.Data!.EntriesPseudonymised >= 1);
        Assert.True(output.Data.TargetsCleared >= 1);

        await using var verification = fixture.CreateContext();

        var scopeEntry = await verification.AuditLogs.SingleAsync(x => x.Id == aboutScope.Id);
        Assert.Null(scopeEntry.ActorPersonId);
        Assert.Equal(scopeId, scopeEntry.TargetId);

        var erasedEntry = await verification.AuditLogs.SingleAsync(x => x.Id == aboutErased.Id);
        Assert.Equal(actor.PublicId, erasedEntry.ActorPersonId);
        Assert.Null(erasedEntry.TargetId);
    }

    [FunctionalFact]
    public async Task GivenAnOldEntryAboutAPerson_WhenThePassRuns_ThenTheListingShowsNoTarget()
    {
        // Given an old self-service entry, whose actor was cleared earlier and whose target is the
        // same, still identifiable, person — found by its target before the pass
        var action = $"AuditRetentionTarget{Guid.NewGuid():N}";
        Guid entryId;
        Guid targetId;

        await using (var context = fixture.CreateContext())
        {
            var person = await SeedPersonAsync(context, anonymisedAt: null);
            var entry = await SeedEntryAsync(
                context, actorId: null, DateTime.UtcNow - AttributionPeriod.Add(TimeSpan.FromDays(30)),
                person.PublicId, action);

            entryId = entry.PublicId;
            targetId = person.PublicId;
        }

        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));
        var before = await ListAsync($"targetId={targetId}");
        Assert.Equal(entryId, Assert.Single(before.Body!.Data!).Id);

        // When
        await using (var context = fixture.CreateContext())
        {
            var output = await Handler(context).HandleAsync(new PseudonymiseAuditActorsCommand());
            Assert.True(output.Success);
        }

        var response = await ListAsync($"action={action}");

        // Then the entry is still listed, with what happened, and names nobody
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var listed = Assert.Single(response.Body!.Data!);
        Assert.Equal(entryId, listed.Id);
        Assert.Equal(action, listed.Action);
        Assert.Null(listed.TargetId);
        Assert.Null(listed.ActorId);

        // And the target filter that found it a moment ago no longer can
        Assert.Empty((await ListAsync($"targetId={targetId}")).Body!.Data!);
    }
}
