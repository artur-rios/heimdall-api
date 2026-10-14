using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Query.Input;
using ArturRios.Heimdall.Query.Output;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Heimdall.Shared.Time;
using ArturRios.Mediator.Query.Interfaces;
using ArturRios.Output;
using FluentValidation;

namespace ArturRios.Heimdall.Query.Handlers;

/// <summary>
///     Handles <see cref="ListAuditLogQuery" /> (UC-48, FR-AT-01/02): the audit trail, newest first,
///     filtered by recording time, actor, action, target and outcome.
/// </summary>
/// <remarks>
///     <para>
///         Authorization is entirely the endpoint's: the only actor is the System Admin, and the
///         <c>RoleRequirement</c> settles it. There is no data-dependent rule left, because the trail
///         has no scope column to scope a Scope Admin's view by.
///     </para>
///     <para>
///         Entries are projected as stored and nothing else is read — no join to <c>PERSON</c>, no
///         inference from neighbouring entries. An attribution cleared under NFR-21 stays cleared
///         here (Data Retention Schedule §7.1); reading the trail must not undo what retention did.
///     </para>
/// </remarks>
public class ListAuditLogQueryHandler(
    IAsyncReadOnlyRepository<AuditLog, long> auditReader,
    IValidator<ListAuditLogQuery> validator)
    : IPaginatedQueryHandlerAsync<ListAuditLogQuery, AuditLogEntryOutput>
{
    public async Task<PaginatedOutput<AuditLogEntryOutput>> HandleAsync(
        ListAuditLogQuery query, CancellationToken cancellationToken = default)
    {
        var output = PaginatedOutput<AuditLogEntryOutput>.New;

        // AF-48a (NFR-10): the range, the action's length and the page, before any query runs.
        var validation = await validator.ValidateAsync(query, cancellationToken);

        if (!validation.IsValid)
        {
            return output.WithErrors(validation.Errors.Select(failure => failure.ErrorMessage));
        }

        var entries = auditReader.Query();

        if (query.From is { } from)
        {
            var fromUtc = UtcInstant.Of(from);
            entries = entries.Where(x => x.CreatedAt >= fromUtc);
        }

        if (query.To is { } to)
        {
            var toUtc = UtcInstant.Of(to);
            entries = entries.Where(x => x.CreatedAt <= toUtc);
        }

        if (query.ActorId is { } actorId)
        {
            entries = entries.Where(x => x.ActorPersonId == actorId);
        }

        // Exact, as recorded. Blank means no filter — what a cleared text box submits.
        if (!string.IsNullOrWhiteSpace(query.Action))
        {
            var action = query.Action;
            entries = entries.Where(x => x.Action == action);
        }

        if (query.TargetId is { } targetId)
        {
            entries = entries.Where(x => x.TargetId == targetId);
        }

        if (query.Succeeded is { } succeeded)
        {
            entries = entries.Where(x => x.Succeeded == succeeded);
        }

        // Newest first, with the internal id as tiebreaker: a command and its audited side effects
        // share an instant often enough that, without it, a page boundary between them could show
        // one entry twice and another never. Ordered on the entity, since the id never reaches the
        // output, then projected and paginated over that ordering.
        var projected = entries
            .OrderByDescending(x => x.CreatedAt)
            .ThenByDescending(x => x.Id)
            .Select(x => new AuditLogEntryOutput
            {
                Id = x.PublicId,
                RecordedAt = x.CreatedAt,
                ActorId = x.ActorPersonId,
                ActorRole = x.ActorRole,
                Action = x.Action,
                TargetId = x.TargetId,
                Succeeded = x.Succeeded,
                FailureReason = x.FailureReason
            });

        var page = await projected.PaginateAsync(query.PageNumber, query.PageSize, orderBy: null);

        return page.WithMessage(AuditMessages.AuditLogRetrieved);
    }
}
