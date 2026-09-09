using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Heimdall.Shared.Retention;
using ArturRios.Mediator.Command.Interfaces;
using ArturRios.Output;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Heimdall.Command.Handlers;

/// <summary>
///     Handles <see cref="PseudonymiseAuditActorsCommand" /> (NFR-21): clears the actor attribution
///     from audit entries past their attribution period, and from entries naming an identity that
///     has since been anonymised. All failures are returned as errors on the
///     <see cref="DataOutput{T}" /> rather than thrown.
/// </summary>
/// <remarks>
///     <para>
///         <b>What this resolves.</b> <c>AUDIT_LOG</c> is where two obligations pull hardest against
///         each other. Accountability is why the trail exists, why it is append-only, and why
///         <c>ActorPersonId</c> is a bare <c>PublicId</c> rather than a foreign key — so an entry
///         survives a hard-deleted person. Storage limitation says an indefinitely retained,
///         unalterable record naming an erased person is personal data processed with no remaining
///         basis. Clearing the attribution and keeping the entry satisfies both: what happened, when,
///         and how often all survive, and once the attribution is gone the entry relates to no
///         identifiable person and stops being personal data at all (GDPR Recital 26, LGPD Art. 12).
///     </para>
///     <para>
///         <b>Why the database is the authority, not this handler.</b> The trigger installed by
///         <c>AllowClearingAuditActor</c> permits exactly one change — the attribution set to NULL,
///         every other column identical, and only once due. This handler cannot clear an attribution
///         early, cannot reassign one, and cannot alter anything else, because the database refuses
///         all three regardless of what the code asks for. The rule lives where it cannot be
///         bypassed by a future caller who did not read this comment.
///     </para>
///     <para>
///         <b>Why an erased actor is not left to the clock.</b> A subject-requested erasure completes
///         thirty days after the request, and the entries it produced days earlier are still well
///         inside the eighteen-month attribution period. Waiting for that period would leave an
///         erased person named in the trail for another seventeen months, which is the outcome the
///         erasure was meant to end.
///     </para>
/// </remarks>
public class PseudonymiseAuditActorsCommandHandler(
    IAsyncReadOnlyRepository<AuditLog> auditReader,
    IAsyncRepository<AuditLog> auditWriter,
    IAsyncReadOnlyRepository<Person> personReader,
    IAsyncReadOnlyRepository<GoogleUser> googleUserReader,
    DataRetentionOptions retention)
    : ICommandHandlerAsync<PseudonymiseAuditActorsCommand, PseudonymiseAuditActorsCommandOutput>
{
    public async Task<DataOutput<PseudonymiseAuditActorsCommandOutput?>> HandleAsync(
        PseudonymiseAuditActorsCommand command)
    {
        var output = DataOutput<PseudonymiseAuditActorsCommandOutput?>.New;
        var cutoff = DateTime.UtcNow - retention.AuditActorRetention;

        // One query with both grounds rather than two passes, so a single batch bound covers the
        // run and an entry qualifying on both grounds is not selected twice.
        var due = await auditReader.Query()
            .Where(entry => entry.ActorPersonId != null
                            && (entry.CreatedAt <= cutoff
                                || personReader.Query().Any(person =>
                                    person.PublicId == entry.ActorPersonId && person.AnonymisedAt != null)
                                || googleUserReader.Query().Any(googleUser =>
                                    googleUser.PublicId == entry.ActorPersonId
                                    && googleUser.AnonymisedAt != null)))
            .OrderBy(entry => entry.CreatedAt)
            .Take(retention.PurgeBatchSize)
            .ToListAsync();

        if (due.Count == 0)
        {
            return Success(output, 0);
        }

        foreach (var entry in due)
        {
            entry.ActorPersonId = null;
            entry.ActorRole = null;
        }

        var update = await auditWriter.UpdateRangeAsync(due);

        return update.Success
            ? Success(output, due.Count)
            : output.WithErrors(update.Errors);
    }

    private static DataOutput<PseudonymiseAuditActorsCommandOutput?> Success(
        DataOutput<PseudonymiseAuditActorsCommandOutput?> output, int entries) =>
        output
            .WithData(new PseudonymiseAuditActorsCommandOutput { EntriesPseudonymised = entries })
            .WithMessage(RetentionMessages.AuditActorsPseudonymised);
}
