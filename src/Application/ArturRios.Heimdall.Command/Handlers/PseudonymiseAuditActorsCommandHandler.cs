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
///     Handles <see cref="PseudonymiseAuditActorsCommand" /> (NFR-21): clears the actor attribution,
///     and any target naming a person or Google User, from audit entries past their attribution
///     period, and from entries naming an identity that has since been anonymised. All failures are
///     returned as errors on the <see cref="DataOutput{T}" /> rather than thrown.
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
///         <b>Why targets too.</b> An entry names a person through <c>TargetId</c> as well as
///         through its actor. For a command somebody runs on their own account the target is their
///         own <c>PublicId</c>, and for an administrator's action on a person it is that person, so
///         clearing the actor alone left those entries resolving to a name and an address for as
///         long as the person's row stayed identifiable (#184). A target naming a person or Google
///         User — in either table, in any deletion state — is therefore cleared on the actor's two
///         grounds: the entry is past the attribution period, or that identity has been anonymised.
///         A target naming anything else (a scope, an application, a credential, a permission) is
///         not personal data and stays, as does one that resolves to nobody, such as a hard-deleted
///         person's.
///     </para>
///     <para>
///         <b>Each part only when it is due.</b> One entry may need its actor cleared, its target
///         cleared, or both, and the two are decided separately: a recent entry by a live
///         administrator about an erased person loses its target and keeps its actor. Nothing that
///         is not due is cleared.
///     </para>
///     <para>
///         <b>Why the database is the authority, not this handler.</b> The trigger installed by
///         <c>AllowClearingAuditTarget</c> permits exactly two changes — the actor attribution set
///         to NULL, and a person target set to NULL — every other column identical, and each only
///         once due. This handler cannot clear either early, cannot reassign either, and cannot
///         alter anything else, because the database refuses all three regardless of what the code
///         asks for. The rule lives where it cannot be bypassed by a future caller who did not read
///         this comment.
///     </para>
///     <para>
///         <b>Why an erased identity is not left to the clock.</b> A subject-requested erasure
///         completes thirty days after the request, and the entries it produced days earlier are
///         still well inside the eighteen-month attribution period. Waiting for that period would
///         leave an erased person named in the trail for another seventeen months, which is the
///         outcome the erasure was meant to end.
///     </para>
/// </remarks>
public class PseudonymiseAuditActorsCommandHandler(
    IAsyncReadOnlyRepository<AuditLog, long> auditReader,
    IAsyncRepository<AuditLog, long> auditWriter,
    IAsyncReadOnlyRepository<Person, long> personReader,
    IAsyncReadOnlyRepository<GoogleUser, long> googleUserReader,
    DataRetentionOptions retention)
    : ICommandHandlerAsync<PseudonymiseAuditActorsCommand, PseudonymiseAuditActorsCommandOutput>
{
    public async Task<DataOutput<PseudonymiseAuditActorsCommandOutput?>> HandleAsync(
        PseudonymiseAuditActorsCommand command, CancellationToken cancellationToken = default)
    {
        var output = DataOutput<PseudonymiseAuditActorsCommandOutput?>.New;
        var cutoff = DateTime.UtcNow - retention.AuditActorRetention;
        var persons = personReader.Query();
        var googleUsers = googleUserReader.Query();

        // One query over the union of both grounds, rather than one per part, so a single batch
        // bound covers the run, an entry needing both is selected once, and each row carries the
        // reason it was selected. The flags are what the loop below acts on, so a row selected for
        // its target cannot have its actor cleared with it, nor the reverse.
        var due = await auditReader.Query()
            .Select(entry => new
            {
                Entry = entry,
                ActorDue = entry.ActorPersonId != null
                           && (entry.CreatedAt <= cutoff
                               || persons.Any(person =>
                                   person.PublicId == entry.ActorPersonId && person.AnonymisedAt != null)
                               || googleUsers.Any(googleUser =>
                                   googleUser.PublicId == entry.ActorPersonId && googleUser.AnonymisedAt != null)),
                TargetDue = entry.TargetId != null
                            && (persons.Any(person =>
                                    person.PublicId == entry.TargetId
                                    && (entry.CreatedAt <= cutoff || person.AnonymisedAt != null))
                                || googleUsers.Any(googleUser =>
                                    googleUser.PublicId == entry.TargetId
                                    && (entry.CreatedAt <= cutoff || googleUser.AnonymisedAt != null)))
            })
            .Where(row => row.ActorDue || row.TargetDue)
            .OrderBy(row => row.Entry.CreatedAt)
            .Take(retention.PurgeBatchSize)
            .ToListAsync();

        if (due.Count == 0)
        {
            return Success(output, 0, 0);
        }

        foreach (var row in due)
        {
            if (row.ActorDue)
            {
                row.Entry.ActorPersonId = null;
                row.Entry.ActorRole = null;
            }

            if (row.TargetDue)
            {
                row.Entry.TargetId = null;
            }
        }

        var update = await auditWriter.UpdateRangeAsync(due.Select(row => row.Entry).ToList());

        return update.Success
            ? Success(output, due.Count(row => row.ActorDue), due.Count(row => row.TargetDue))
            : output.WithErrors(update.Errors);
    }

    private static DataOutput<PseudonymiseAuditActorsCommandOutput?> Success(
        DataOutput<PseudonymiseAuditActorsCommandOutput?> output, int actorsCleared, int targetsCleared) =>
        output
            .WithData(new PseudonymiseAuditActorsCommandOutput
            {
                EntriesPseudonymised = actorsCleared,
                TargetsCleared = targetsCleared
            })
            .WithMessage(RetentionMessages.AuditActorsPseudonymised);
}
