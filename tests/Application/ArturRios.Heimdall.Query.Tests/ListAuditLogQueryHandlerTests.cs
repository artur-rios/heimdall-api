using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Query.Handlers;
using ArturRios.Heimdall.Query.Input;
using ArturRios.Heimdall.Query.Input.Validation;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Mock;

namespace ArturRios.Heimdall.Query.Tests;

// Unit tests for ListAuditLogQueryHandler (UC-48, FR-AT-01/02): newest first with the internal id as
// tiebreaker, every filter alone and combined, inclusive bounds, the projection as stored (a cleared
// actor stays null), AF-48a (a backwards range, an over-long action, an out-of-bounds page) and
// AF-48c (nothing matches → an empty page, not a refusal). AF-48b is the endpoint's RoleRequirement
// and is asserted in AuditControllerListTests.
public class ListAuditLogQueryHandlerTests
{
    private static readonly DateTime T0 = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

    private static AuditLog Entry(
        long id,
        DateTime createdAt,
        string action = "CreateScopeCommand",
        Guid? actor = null,
        int? actorRole = null,
        Guid? target = null,
        bool succeeded = true,
        string? failureReason = null) => new()
    {
        Id = id,
        PublicId = Guid.NewGuid(),
        CreatedAt = createdAt,
        Action = action,
        ActorPersonId = actor,
        ActorRole = actorRole,
        TargetId = target,
        Succeeded = succeeded,
        FailureReason = failureReason
    };

    private static async Task<AsyncFakeRepository<AuditLog, long>> EntriesWith(params AuditLog[] entries)
    {
        var repository = new AsyncFakeRepository<AuditLog, long>();

        foreach (var entry in entries)
        {
            await repository.CreateAsync(entry);
        }

        return repository;
    }

    private static ListAuditLogQueryHandler Handler(AsyncFakeRepository<AuditLog, long> entries) =>
        new(entries, new ListAuditLogQueryValidator());

    private static ListAuditLogQuery Query() => new()
    {
        PageNumber = 1,
        PageSize = 20,
        ActingPersonId = Guid.NewGuid(),
        ActingRole = (int)Roles.SystemAdmin
    };

    [UnitFact]
    public async Task GivenEntries_WhenListingAuditLog_ThenNewestComesFirst()
    {
        // Given three entries an hour apart, stored out of order
        var oldest = Entry(1, T0);
        var newest = Entry(2, T0.AddHours(2));
        var middle = Entry(3, T0.AddHours(1));
        var handler = Handler(await EntriesWith(oldest, newest, middle));

        // When
        var output = await handler.HandleAsync(Query());

        // Then
        Assert.True(output.Success);
        Assert.Contains(AuditMessages.AuditLogRetrieved, output.Messages);
        Assert.Equal(3, output.TotalItems);
        Assert.Equal([newest.PublicId, middle.PublicId, oldest.PublicId], output.Data!.Select(x => x.Id));
    }

    [UnitFact]
    public async Task GivenEntriesRecordedAtOneInstant_WhenListingAuditLog_ThenTheLaterIdComesFirst()
    {
        // Given two entries sharing a timestamp — a command and its side effect can. The id breaks
        // the tie, so a page boundary between them cannot show one twice and the other never.
        var first = Entry(1, T0);
        var second = Entry(2, T0);
        var handler = Handler(await EntriesWith(first, second));

        // When
        var output = await handler.HandleAsync(Query());

        // Then
        Assert.Equal([second.PublicId, first.PublicId], output.Data!.Select(x => x.Id));
    }

    [UnitFact]
    public async Task GivenARefusedEntry_WhenListingAuditLog_ThenEveryFieldIsProjectedAsStored()
    {
        // Given a refusal with an actor, a role, a target and a canonical reason
        var actor = Guid.NewGuid();
        var target = Guid.NewGuid();
        var entry = Entry(
            1, T0, action: "DeletePersonCommand", actor: actor, actorRole: (int)Roles.ScopeAdmin,
            target: target, succeeded: false, failureReason: PersonMessages.NotAuthorizedToDeletePerson);
        var handler = Handler(await EntriesWith(entry));

        // When
        var output = await handler.HandleAsync(Query());

        // Then
        var item = Assert.Single(output.Data!);
        Assert.Equal(entry.PublicId, item.Id);
        Assert.Equal(T0, item.RecordedAt);
        Assert.Equal(actor, item.ActorId);
        Assert.Equal((int)Roles.ScopeAdmin, item.ActorRole);
        Assert.Equal("DeletePersonCommand", item.Action);
        Assert.Equal(target, item.TargetId);
        Assert.False(item.Succeeded);
        Assert.Equal(PersonMessages.NotAuthorizedToDeletePerson, item.FailureReason);
    }

    [UnitFact]
    public async Task GivenAnEntryWhoseActorWasCleared_WhenListingAuditLog_ThenActorFieldsAreNull()
    {
        // Given an entry the retention pass has pseudonymised (NFR-21) — nothing to reconstruct from
        var entry = Entry(1, T0, actor: null, actorRole: null, target: Guid.NewGuid());
        var handler = Handler(await EntriesWith(entry));

        // When
        var output = await handler.HandleAsync(Query());

        // Then
        var item = Assert.Single(output.Data!);
        Assert.Null(item.ActorId);
        Assert.Null(item.ActorRole);
    }

    [UnitFact]
    public async Task GivenFromAndTo_WhenListingAuditLog_ThenBothBoundsAreInclusive()
    {
        // Given one entry just before the range, one on each bound, one just after
        var before = Entry(1, T0.AddSeconds(-1));
        var onFrom = Entry(2, T0);
        var onTo = Entry(3, T0.AddHours(1));
        var after = Entry(4, T0.AddHours(1).AddSeconds(1));
        var handler = Handler(await EntriesWith(before, onFrom, onTo, after));
        var query = Query();
        query.From = T0;
        query.To = T0.AddHours(1);

        // When
        var output = await handler.HandleAsync(query);

        // Then
        Assert.Equal([onTo.PublicId, onFrom.PublicId], output.Data!.Select(x => x.Id));
    }

    [UnitFact]
    public async Task GivenToEqualToFrom_WhenListingAuditLog_ThenTheEntryAtThatInstantIsReturned()
    {
        // Given a range of a single instant — valid, since To is not earlier than From
        var exact = Entry(1, T0);
        var handler = Handler(await EntriesWith(exact, Entry(2, T0.AddSeconds(1))));
        var query = Query();
        query.From = T0;
        query.To = T0;

        // When
        var output = await handler.HandleAsync(query);

        // Then
        Assert.True(output.Success);
        Assert.Equal([exact.PublicId], output.Data!.Select(x => x.Id));
    }

    [UnitFact]
    public async Task GivenBoundsWithoutAnOffset_WhenListingAuditLog_ThenTheyFilterAsUtc()
    {
        // Given From as the binder produces it from "2026-10-01T12:00:00" — no offset
        var onFrom = Entry(1, T0);
        var handler = Handler(await EntriesWith(Entry(2, T0.AddSeconds(-1)), onFrom));
        var query = Query();
        query.From = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Unspecified);

        // When
        var output = await handler.HandleAsync(query);

        // Then
        Assert.True(output.Success);
        Assert.Equal([onFrom.PublicId], output.Data!.Select(x => x.Id));
    }

    [UnitFact]
    public async Task GivenAnActorFilter_WhenListingAuditLog_ThenOnlyThatActorsEntriesAreReturned()
    {
        // Given entries by two actors and one anonymous entry
        var ana = Guid.NewGuid();
        var anas = Entry(1, T0, actor: ana, actorRole: (int)Roles.SystemAdmin);
        var handler = Handler(await EntriesWith(
            anas, Entry(2, T0, actor: Guid.NewGuid(), actorRole: (int)Roles.ScopeAdmin), Entry(3, T0)));
        var query = Query();
        query.ActorId = ana;

        // When
        var output = await handler.HandleAsync(query);

        // Then
        Assert.Equal([anas.PublicId], output.Data!.Select(x => x.Id));
    }

    [UnitFact]
    public async Task GivenAnActionFilter_WhenListingAuditLog_ThenOnlyThatActionIsReturned()
    {
        // Given two kinds of action
        var create = Entry(1, T0, action: "CreateScopeCommand");
        var handler = Handler(await EntriesWith(create, Entry(2, T0, action: "UpdateScopeCommand")));
        var query = Query();
        query.Action = "CreateScopeCommand";

        // When
        var output = await handler.HandleAsync(query);

        // Then
        Assert.Equal([create.PublicId], output.Data!.Select(x => x.Id));
    }

    [UnitFact]
    public async Task GivenAnActionInAnotherCase_WhenListingAuditLog_ThenNothingMatches()
    {
        // Given the action is matched exactly, as recorded — the command's type name
        var handler = Handler(await EntriesWith(Entry(1, T0, action: "CreateScopeCommand")));
        var query = Query();
        query.Action = "createscopecommand";

        // When
        var output = await handler.HandleAsync(query);

        // Then
        Assert.True(output.Success);
        Assert.Empty(output.Data!);
    }

    [UnitTheory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GivenABlankActionFilter_WhenListingAuditLog_ThenItIsIgnored(string action)
    {
        // Given what a cleared text box submits
        var handler = Handler(await EntriesWith(
            Entry(1, T0, action: "CreateScopeCommand"), Entry(2, T0, action: "UpdateScopeCommand")));
        var query = Query();
        query.Action = action;

        // When
        var output = await handler.HandleAsync(query);

        // Then — no filter, not "match nothing"
        Assert.True(output.Success);
        Assert.Equal(2, output.TotalItems);
    }

    [UnitFact]
    public async Task GivenATargetFilter_WhenListingAuditLog_ThenOnlyThatTargetsEntriesAreReturned()
    {
        // Given entries about two entities and one with no target
        var target = Guid.NewGuid();
        var about = Entry(1, T0, target: target);
        var handler = Handler(await EntriesWith(about, Entry(2, T0, target: Guid.NewGuid()), Entry(3, T0)));
        var query = Query();
        query.TargetId = target;

        // When
        var output = await handler.HandleAsync(query);

        // Then
        Assert.Equal([about.PublicId], output.Data!.Select(x => x.Id));
    }

    [UnitTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GivenAnOutcomeFilter_WhenListingAuditLog_ThenOnlyThatOutcomeIsReturned(bool succeeded)
    {
        // Given one completed write and one refusal
        var completed = Entry(1, T0, succeeded: true);
        var refused = Entry(2, T0, succeeded: false, failureReason: ScopeMessages.NotScopeOwner);
        var handler = Handler(await EntriesWith(completed, refused));
        var query = Query();
        query.Succeeded = succeeded;

        // When
        var output = await handler.HandleAsync(query);

        // Then
        var expected = succeeded ? completed : refused;
        Assert.Equal([expected.PublicId], output.Data!.Select(x => x.Id));
    }

    [UnitFact]
    public async Task GivenSeveralFilters_WhenListingAuditLog_ThenTheyCombineWithAnd()
    {
        // Given entries matching the actor only, the action only, and both
        var ana = Guid.NewGuid();
        var both = Entry(1, T0, action: "CreateScopeCommand", actor: ana, actorRole: (int)Roles.SystemAdmin);
        var handler = Handler(await EntriesWith(
            both,
            Entry(2, T0, action: "UpdateScopeCommand", actor: ana, actorRole: (int)Roles.SystemAdmin),
            Entry(3, T0, action: "CreateScopeCommand", actor: Guid.NewGuid(), actorRole: (int)Roles.SystemAdmin)));
        var query = Query();
        query.ActorId = ana;
        query.Action = "CreateScopeCommand";

        // When
        var output = await handler.HandleAsync(query);

        // Then
        Assert.Equal([both.PublicId], output.Data!.Select(x => x.Id));
    }

    [UnitFact]
    public async Task GivenNoMatchingEntry_WhenListingAuditLog_ThenAnEmptyPageIsASuccess()
    {
        // Given AF-48c — entries exist, none about this target
        var handler = Handler(await EntriesWith(Entry(1, T0, target: Guid.NewGuid())));
        var query = Query();
        query.TargetId = Guid.NewGuid();

        // When
        var output = await handler.HandleAsync(query);

        // Then
        Assert.True(output.Success);
        Assert.Equal(0, output.TotalItems);
        Assert.Empty(output.Data!);
        Assert.Contains(AuditMessages.AuditLogRetrieved, output.Messages);
    }

    [UnitFact]
    public async Task GivenToEarlierThanFrom_WhenListingAuditLog_ThenReturnsInvalidDateRange()
    {
        // Given AF-48a — a range that runs backwards
        var handler = Handler(await EntriesWith(Entry(1, T0)));
        var query = Query();
        query.From = T0;
        query.To = T0.AddSeconds(-1);

        // When
        var output = await handler.HandleAsync(query);

        // Then
        Assert.False(output.Success);
        Assert.Contains(AuditMessages.InvalidDateRange, output.Errors);
    }

    [UnitFact]
    public async Task GivenAnActionLongerThanTheColumn_WhenListingAuditLog_ThenReturnsFilterTooLong()
    {
        // Given AF-48a — AUDIT_LOG.action holds 200 characters, so 201 could never match
        var handler = Handler(await EntriesWith(Entry(1, T0)));
        var query = Query();
        query.Action = new string('A', 201);

        // When
        var output = await handler.HandleAsync(query);

        // Then
        Assert.False(output.Success);
        Assert.Contains(PaginationMessages.FilterTooLong, output.Errors);
    }

    [UnitFact]
    public async Task GivenAnActionAtTheColumnsLength_WhenListingAuditLog_ThenItIsAccepted()
    {
        // Given the boundary — exactly 200 characters
        var handler = Handler(await EntriesWith(Entry(1, T0)));
        var query = Query();
        query.Action = new string('A', 200);

        // When
        var output = await handler.HandleAsync(query);

        // Then
        Assert.True(output.Success);
        Assert.Empty(output.Data!);
    }

    [UnitFact]
    public async Task GivenPageSizeAboveTheBound_WhenListingAuditLog_ThenReturnsInvalidPageSize()
    {
        // Given AF-48a (NFR-10)
        var handler = Handler(await EntriesWith(Entry(1, T0)));
        var query = Query();
        query.PageSize = 101;

        // When
        var output = await handler.HandleAsync(query);

        // Then
        Assert.False(output.Success);
        Assert.Contains(PaginationMessages.InvalidPageSize, output.Errors);
    }
}
