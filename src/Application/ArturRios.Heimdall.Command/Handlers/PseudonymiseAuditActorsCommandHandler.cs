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
        var result = new PseudonymiseAuditActorsCommandOutput();

        // A batch at a time, repeated while a batch comes back full (#206): one batch a run held the
        // trail to 12,000 clearings a day at the defaults, which a busy deployment outgrows for good.
        var entriesFull = false;

        for (var batch = 0; batch < retention.MaxBatchesPerTick; batch++)
        {
            var due = await SelectDueAsync(cutoff);

            if (due.Count == 0)
            {
                entriesFull = false;
                break;
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

            if (!update.Success)
            {
                // What earlier batches cleared stays cleared, and is reported with the failure.
                return output.WithData(result).WithErrors(update.Errors);
            }

            result.EntriesPseudonymised += due.Count(row => row.ActorDue);
            result.TargetsCleared += due.Count(row => row.TargetDue);

            entriesFull = due.Count == retention.PurgeBatchSize;

            if (!entriesFull)
            {
                break;
            }
        }

        // After the clearing, so a tombstone whose last entry this run cleared goes in the same run.
        var settledBefore = DateTime.UtcNow - TombstoneGracePeriod;
        var tombstonesFull = false;

        for (var batch = 0; batch < retention.MaxBatchesPerTick; batch++)
        {
            var settled = await Settled(settledBefore)
                .OrderBy(tombstone => tombstone.CreatedAt)
                .Select(tombstone => tombstone.Id)
                .Take(retention.PurgeBatchSize)
                .ToListAsync();

            if (settled.Count == 0)
            {
                tombstonesFull = false;
                break;
            }

            var removal = await erasedSubjectWriter.DeleteRangeAsync(settled);

            if (!removal.Success)
            {
                return output.WithData(result).WithErrors(removal.Errors);
            }

            result.TombstonesRemoved += settled.Count;
            tombstonesFull = settled.Count == retention.PurgeBatchSize;

            if (!tombstonesFull)
            {
                break;
            }
        }

        // Counted only where the run stopped on a full batch: one that was not full took all that was due.
        result.Backlog = (entriesFull ? await CountDueAsync(cutoff) : 0)
                         + (tombstonesFull ? await Settled(settledBefore).CountAsync() : 0);

        return output
            .WithData(result)
            .WithMessage(RetentionMessages.AuditActorsPseudonymised);
    }

    /// <summary>One batch of the entries due, oldest first, each with the parts of it that are due.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Simple queries, not one (#206).</b> The single query this replaced evaluated up to
    ///         four correlated subqueries for every attributed entry in the attribution window, in
    ///         <c>created_at</c> order, so a run with nothing due still read the whole window, on every
    ///         instance, every run. Each ground is now its own query that projects ids rather than
    ///         entities, and each is served by an index. The two grounds of age read a partial index on
    ///         <c>created_at</c> over the entries that still have an actor, or a target
    ///         (<c>AddAuditRetentionIndexes</c>), from the oldest up to the cutoff: an actor past the period is
    ///         always cleared, so that range holds only the backlog; a target past the period stays when it
    ///         names a scope or anything else that is not a person, so the target range also holds those,
    ///         each checked against the identities by their unique index. The grounds of identity are
    ///         reached from the anonymised identities and the tombstones, through the indexes on
    ///         <c>actor_person_id</c> and <c>target_id</c>, so their cost follows the identities rather than
    ///         the trail (see <see cref="DueGroundsAsync" />).
    ///     </para>
    ///     <para>
    ///         <b>One batch bound still covers the run.</b> Each query takes its own oldest batch, by
    ///         <c>created_at</c> and then id; the batch is the oldest of their union. An entry in it is
    ///         necessarily in the batch of every ground it is due on — nothing older than it is due on that
    ///         ground than is due at all — so the grounds a selected entry was returned by are exactly the
    ///         parts of it that are due. That is what the clearing acts on, so a row selected for its target
    ///         cannot have its actor cleared with it, nor the reverse, and an entry due on both grounds is
    ///         selected, written and counted once, as before.
    ///     </para>
    /// </remarks>
    private async Task<List<DueEntry>> SelectDueAsync(DateTime cutoff)
    {
        var size = retention.PurgeBatchSize;
        var grounds = await DueGroundsAsync(cutoff);

        var actorDue = new HashSet<long>();
        var targetDue = new HashSet<long>();
        var candidates = new Dictionary<long, DateTime>();

        foreach (var (ground, isActor) in grounds)
        {
            var batch = await ground
                .OrderBy(entry => entry.CreatedAt)
                .ThenBy(entry => entry.Id)
                .Select(entry => new { entry.Id, entry.CreatedAt })
                .Take(size)
                .ToListAsync();

            foreach (var row in batch)
            {
                candidates[row.Id] = row.CreatedAt;
                (isActor ? actorDue : targetDue).Add(row.Id);
            }
        }

        var ids = candidates
            .OrderBy(candidate => candidate.Value)
            .ThenBy(candidate => candidate.Key)
            .Take(size)
            .Select(candidate => candidate.Key)
            .ToList();

        if (ids.Count == 0)
        {
            return [];
        }

        // By primary key: the batch is already chosen, and these are the rows the write needs.
        var entries = await auditReader.Query()
            .Where(entry => ids.Contains(entry.Id))
            .ToListAsync();

        return entries
            .OrderBy(entry => entry.CreatedAt)
            .ThenBy(entry => entry.Id)
            .Select(entry => new DueEntry
            {
                Entry = entry,
                ActorDue = actorDue.Contains(entry.Id),
                TargetDue = targetDue.Contains(entry.Id)
            })
            .ToList();
    }

    /// <summary>How many entries are still due: the backlog a run that stopped at its cap leaves.</summary>
    private async Task<int> CountDueAsync(DateTime cutoff)
    {
        var due = (await DueGroundsAsync(cutoff))
            .Select(ground => ground.Ground.Select(entry => entry.Id))
            .Aggregate((all, next) => all.Union(next));

        return await due.CountAsync();
    }

    /// <summary>
    ///     The grounds on which an entry is due, each as a query over the entries due on it, and whether it
    ///     clears the actor (otherwise the target). An entry is due when it is due on any of them.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Together these are exactly the rule the trigger enforces. The actor is due past the
    ///         attribution period, or once the identity it names is anonymised. A target is due when it names
    ///         a person or Google User and the entry is past the period, or when that identity is anonymised,
    ///         or — at any age — when its id was tombstoned by a hard delete and no <c>person</c> or
    ///         <c>google_user</c> row has it any more (#195).
    ///     </para>
    ///     <para>
    ///         The grounds of identity are asked in two steps. First, which anonymised or hard-deleted
    ///         identities the trail still names, asked from the identities' side: one probe of the index on
    ///         <c>actor_person_id</c> or <c>target_id</c> each, so that when nothing is due — the usual case —
    ///         the cost is the identities, not the trail. Then the entries naming those, by id. Asked in one
    ///         step, as entries whose actor is in a subquery, PostgreSQL may walk <c>created_at</c> in order,
    ///         or scan the table, expecting to meet such entries early: measured, it scanned the whole trail
    ///         when nothing was due.
    ///     </para>
    /// </remarks>
    private async Task<List<(IQueryable<AuditLog> Ground, bool IsActor)>> DueGroundsAsync(DateTime cutoff)
    {
        var entries = auditReader.Query();
        var persons = personReader.Query();
        var googleUsers = googleUserReader.Query();

        var identities = persons.Select(person => person.PublicId)
            .Concat(googleUsers.Select(googleUser => googleUser.PublicId));

        var anonymised = persons.Where(person => person.AnonymisedAt != null).Select(person => person.PublicId)
            .Concat(googleUsers.Where(googleUser => googleUser.AnonymisedAt != null)
                .Select(googleUser => googleUser.PublicId));

        var hardDeleted = erasedSubjectReader.Query()
            .Where(tombstone => !persons.Any(person => person.PublicId == tombstone.Id)
                                && !googleUsers.Any(googleUser => googleUser.PublicId == tombstone.Id))
            .Select(tombstone => tombstone.Id);

        var namedAsActor = await anonymised
            .Where(id => entries.Any(entry => entry.ActorPersonId == id))
            .ToListAsync();

        var namedAsTarget = await anonymised.Concat(hardDeleted)
            .Where(id => entries.Any(entry => entry.TargetId == id))
            .ToListAsync();

        List<(IQueryable<AuditLog> Ground, bool IsActor)> grounds =
        [
            // The actor, past the period: the partial index on created_at over attributed entries.
            (entries.Where(entry => entry.ActorPersonId != null && entry.CreatedAt <= cutoff), true),

            // A person target, past the period: the partial index on created_at over entries with a target.
            (entries.Where(entry => entry.TargetId != null
                                    && entry.CreatedAt <= cutoff
                                    && identities.Contains(entry.TargetId.Value)), false)
        ];

        // The actor or a target anonymised, or a target hard-deleted: the entries naming those still named.
        if (namedAsActor.Count > 0)
        {
            grounds.Add((entries.Where(entry =>
                entry.ActorPersonId != null && namedAsActor.Contains(entry.ActorPersonId.Value)), true));
        }

        if (namedAsTarget.Count > 0)
        {
            grounds.Add((entries.Where(entry =>
                entry.TargetId != null && namedAsTarget.Contains(entry.TargetId.Value)), false));
        }

        return grounds;
    }

    /// <summary>The tombstones past their grace that no audit entry names as its target any more.</summary>
    private IQueryable<ErasedSubject> Settled(DateTime settledBefore)
    {
        var entries = auditReader.Query();

        return erasedSubjectReader.Query()
            .Where(tombstone => tombstone.CreatedAt <= settledBefore
                                && !entries.Any(entry => entry.TargetId == tombstone.Id));
    }

    /// <summary>An entry due for clearing, and which of its two parts are due.</summary>
    private sealed class DueEntry
    {
        public AuditLog Entry { get; init; } = null!;

        public bool ActorDue { get; init; }

        public bool TargetDue { get; init; }
    }
}
