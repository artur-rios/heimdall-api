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
///     period, and from entries naming an identity that has since been anonymised or hard-deleted;
///     then removes the tombstones of hard-deleted identities that no entry names any more. All
///     failures are returned as errors on the <see cref="DataOutput{T}" /> rather than thrown.
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
///         not personal data and stays.
///     </para>
///     <para>
///         <b>Hard-deleted identities (#195).</b> A target whose row was hard-deleted resolves to
///         nobody here, but the id still names that person to anything holding a copy of the old
///         row — a backup, an export, a log line — and to the hard delete's own entry, written just
///         after the row went. Every hard delete therefore tombstones the id first
///         (<see cref="ErasedSubject" />). A target is also due, at any age, when its id is
///         tombstoned and no <c>person</c> or <c>google_user</c> row has it: the tombstone says it
///         was a person's, and the missing row says the delete really happened, so a tombstone left
///         by a delete that failed clears nothing. An id that resolves to no row and was never
///         tombstoned — a hard-deleted scope's, or a person's removed before tombstones existed —
///         stays as recorded, because nothing says it was ever a person's.
///     </para>
///     <para>
///         <b>Tombstones are short-lived.</b> After clearing, the run removes the tombstones that
///         are older than <see cref="TombstoneGracePeriod" /> and that no entry names as its target
///         any more. A tombstone named by an entry the batch did not reach waits for a later run.
///     </para>
///     <para>
///         <b>Each part only when it is due.</b> One entry may need its actor cleared, its target
///         cleared, or both, and the two are decided separately: a recent entry by a live
///         administrator about an erased person loses its target and keeps its actor. Nothing that
///         is not due is cleared.
///     </para>
///     <para>
///         <b>Why the database is the authority, not this handler.</b> The trigger installed by
///         <c>AllowClearingAuditTarget</c>, and widened by <c>AllowClearingErasedSubjectTarget</c>,
///         permits exactly two changes — the actor attribution set to NULL, and a person target set to
///         NULL — every other column identical, and each only once due. This handler cannot clear
///         either early, cannot reassign either, and cannot alter anything else, because the database
///         refuses all three regardless of what the code asks for. The rule lives where it cannot be
///         bypassed by a future caller who did not read this comment.
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
    IAsyncReadOnlyRepository<ErasedSubject, Guid> erasedSubjectReader,
    IAsyncRepository<ErasedSubject, Guid> erasedSubjectWriter,
    DataRetentionOptions retention)
    : ICommandHandlerAsync<PseudonymiseAuditActorsCommand, PseudonymiseAuditActorsCommandOutput>
{
    /// <summary>
    ///     How long a tombstone is kept even when no audit entry names it: one day.
    /// </summary>
    /// <remarks>
    ///     The hard delete's own audit entry, which names the removed id as its target, is written by
    ///     the auditing decorator after the handler returns, so for a moment the tombstone exists and
    ///     nothing names it yet. A run in that moment must not remove it, or that entry would keep the
    ///     id for good. A day is far longer than that moment and short enough that a tombstone is not
    ///     a lasting record of the person in its own right.
    /// </remarks>
    public static readonly TimeSpan TombstoneGracePeriod = TimeSpan.FromDays(1);

    public async Task<DataOutput<PseudonymiseAuditActorsCommandOutput?>> HandleAsync(
        PseudonymiseAuditActorsCommand command, CancellationToken cancellationToken = default)
    {
        var output = DataOutput<PseudonymiseAuditActorsCommandOutput?>.New;
        var cutoff = DateTime.UtcNow - retention.AuditActorRetention;
        var persons = personReader.Query();
        var googleUsers = googleUserReader.Query();
        var tombstones = erasedSubjectReader.Query();

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
                                    && (entry.CreatedAt <= cutoff || googleUser.AnonymisedAt != null))
                                || (tombstones.Any(tombstone => tombstone.Id == entry.TargetId)
                                    && !persons.Any(person => person.PublicId == entry.TargetId)
                                    && !googleUsers.Any(googleUser => googleUser.PublicId == entry.TargetId)))
            })
            .Where(row => row.ActorDue || row.TargetDue)
            .OrderBy(row => row.Entry.CreatedAt)
            .Take(retention.PurgeBatchSize)
            .ToListAsync();

        if (due.Count > 0)
        {
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

            if (!update.Success)
            {
                return output.WithErrors(update.Errors);
            }
        }

        // After the clearing, so a tombstone whose last entry this run cleared goes in the same run.
        var settledBefore = DateTime.UtcNow - TombstoneGracePeriod;
        var entries = auditReader.Query();
        var settled = await tombstones
            .Where(tombstone => tombstone.CreatedAt <= settledBefore
                                && !entries.Any(entry => entry.TargetId == tombstone.Id))
            .OrderBy(tombstone => tombstone.CreatedAt)
            .Select(tombstone => tombstone.Id)
            .Take(retention.PurgeBatchSize)
            .ToListAsync();

        if (settled.Count > 0)
        {
            var removal = await erasedSubjectWriter.DeleteRangeAsync(settled);

            if (!removal.Success)
            {
                return output.WithErrors(removal.Errors);
            }
        }

        return output
            .WithData(new PseudonymiseAuditActorsCommandOutput
            {
                EntriesPseudonymised = due.Count(row => row.ActorDue),
                TargetsCleared = due.Count(row => row.TargetDue),
                TombstonesRemoved = settled.Count
            })
            .WithMessage(RetentionMessages.AuditActorsPseudonymised);
    }
}
