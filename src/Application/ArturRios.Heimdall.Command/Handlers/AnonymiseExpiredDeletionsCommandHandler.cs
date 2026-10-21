using ArturRios.Data.Relational.Core.Entities;
using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Heimdall.Command.Auditing;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Command.Services;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Domain.Persistence;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Heimdall.Shared.Retention;
using ArturRios.Mediator.Command.Interfaces;
using ArturRios.Output;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ArturRios.Heimdall.Command.Handlers;

/// <summary>
///     Handles <see cref="AnonymiseExpiredDeletionsCommand" /> (NFR-20): finds logically deleted
///     persons and Google Users whose retention window has run out, removes the dependent rows that
///     hold their data, and overwrites every identifying value on the record itself through
///     <see cref="IdentityAnonymiser" />. All failures are returned as errors on the
///     <see cref="DataOutput{T}" /> rather than thrown.
/// </summary>
/// <remarks>
///     <para>
///         <b>Which deadline applies.</b> The one the record's <c>DeletionKind</c> names. A subject
///         who asked to be erased gets the statutory deadline; an administrative deletion gets the
///         longer reversal window (Data Retention Schedule §4). A record deleted before this
///         mechanism existed carries no kind at all, and is treated as administrative — the longer
///         of the two, because guessing that somebody had requested erasure would be inventing a
///         request that may never have been made.
///     </para>
///     <para>
///         <b>Why the dependents go first.</b> Anonymising the person while leaving their password
///         reset tokens and two-factor configuration in place would erase the name and keep the
///         material: a TOTP secret and a recovery code hash belong to the person, and a reset token
///         addresses the mailbox that was just disowned. The token purge would reach the tokens
///         eventually, on its own much shorter window, but "eventually" is not what an erasure
///         obligation accepts, and it would never reach the two-factor rows at all.
///     </para>
///     <para>
///         <b>Invitations.</b> A person anonymised has their unended scope owner invitations voided and
///         is cleared as the inviter of the rest (FR-SC-17); each void is audited, on its invitation, as
///         soon as it is made (<see cref="ErasedPersonInvitations" />). The voids follow the anonymisation
///         rather than preceding it with the dependents, because only the targeted write knows whether the
///         person is erased at all. A run that anonymised a person and then failed before forgetting them
///         is caught up by the next one, which first forgets every anonymised person an invitation still
///         names.
///     </para>
///     <para>
///         <b>What is deliberately left alone.</b> The scope membership and ownership rows, the
///         applications an owner held, and the audit entries naming them. All are foreign keys or
///         accountability records that NFR-07 requires to keep resolving, and none carries an
///         identifying value once the record they point at has been anonymised. The audit trail's
///         own actor reference is #97's, not this pass's — a different mechanism for a table that
///         refuses updates by design.
///     </para>
///     <para>
///         <b>NFR-12 is untouched.</b> Anonymisation changes no <c>SCOPE_OWNER</c> row, so a scope
///         keeps exactly the owners it had. The guarantee is not weakened here because it was
///         already enforced at deletion time: UC-09 refuses to logically delete a scope's last
///         owner, so no record reaching this pass was one.
///     </para>
/// </remarks>
public class AnonymiseExpiredDeletionsCommandHandler(
    IAsyncReadOnlyRepository<Person, long> personReader,
    IAsyncReadOnlyRepository<GoogleUser, long> googleUserReader,
    IAsyncRepository<GoogleUser, long> googleUserWriter,
    IAsyncRepository<PasswordResetToken, long> passwordResetTokenWriter,
    IAsyncRepository<EmailVerificationToken, long> emailVerificationTokenWriter,
    IAsyncRepository<TwoFactorAuth, long> twoFactorAuthWriter,
    DataRetentionOptions retention,
    IAtomicWrites atomicWrites,
    IScopeOwnerInvitations invitations,
    IAsyncReadOnlyRepository<ScopeOwnerInvitation, long> invitationReader,
    IAuditLogWriter auditLog,
    ILogger<AnonymiseExpiredDeletionsCommandHandler> logger,
    IWriteTransactions transactions,
    IInvariantLocks locks)
    : ICommandHandlerAsync<AnonymiseExpiredDeletionsCommand, AnonymiseExpiredDeletionsCommandOutput>
{
    /// <summary>
    ///     <c>DeletionKinds.SubjectRequested</c> as a plain <c>int</c>. The comparison happens inside
    ///     an expression EF Core translates to SQL, and a cast of an enum member there is one more
    ///     thing for the provider to have an opinion about than it needs to be.
    /// </summary>
    private const int SubjectRequested = (int)DeletionKinds.SubjectRequested;

    public async Task<DataOutput<AnonymiseExpiredDeletionsCommandOutput?>> HandleAsync(
        AnonymiseExpiredDeletionsCommand command, CancellationToken cancellationToken = default)
    {
        var output = DataOutput<AnonymiseExpiredDeletionsCommandOutput?>.New;
        var now = DateTime.UtcNow;

        // Two cutoffs rather than one, because the deadline depends on why the record was deleted.
        // They are the fallback: where the subject made a request (UC-42), the deadline was computed
        // and stored when the request attached, and that stored value wins — recomputing it from the
        // current configuration would let a later change move an obligation already owed. The
        // cutoffs still apply to an administrative deletion, which has no stored deadline, and to a
        // subject-requested record that somehow lacks one, which would otherwise silently fall to
        // the longer window.
        var erasureCutoff = now - retention.SubjectErasureDeadline;
        var administrativeCutoff = now - retention.AdministrativeDeletionWindow;

        // A request NFR-12 blocked at the time it was made may have become possible since — an owner
        // transferred, a co-owner added (UC-21). Re-checked here rather than requiring an
        // administrator to resubmit anything: the subject asked once, and the deadline has been
        // running the whole time.
        //
        // Best-effort (#210): a request the retry cannot save is reported with the run's errors and
        // does not stop the due identities below, whose statutory deadlines are running regardless.
        var retryErrors = await RetryBlockedRequestsAsync(now);

        // Before selecting anyone new: a person anonymised by an earlier run that failed before their
        // invitations were forgotten is never selected again, and would otherwise stay named for good.
        var caughtUp = await ForgetAnonymisedStillNamedAsync(now);

        var result = new AnonymiseExpiredDeletionsCommandOutput { ScopeOwnerInvitationsVoided = caughtUp.Count };
        var anonymisedIds = new List<Guid>();
        var errors = new List<string>();

        // A batch at a time, repeated while a batch comes back full (#206): one batch a run left a large
        // scope's deleted members waiting days past their deadline.
        var personsFull = false;
        var googleUsersFull = false;
        var interrupted = false;

        // Thrown part-way, the run still reports what it did: the identities anonymised before the
        // failure are erased, and the ledger has to name them (NFR-25), from every batch of the run.
        try
        {
            for (var batch = 0; batch < retention.MaxBatchesPerTick; batch++)
            {
                var persons = await DuePersons(now, erasureCutoff, administrativeCutoff)
                    .OrderBy(person => person.DeletedAt)
                    .Take(retention.PurgeBatchSize)
                    .ToListAsync();

                if (persons.Count == 0)
                {
                    personsFull = false;
                    break;
                }

                var (removed, dependentErrors) = await RemovePersonDependentsAsync(persons);

                result.DependentsRemoved += removed;

                // A failed dependent removal stops the run before the record is overwritten. Anonymising
                // anyway would leave the person's two-factor secret behind with nothing left to connect it
                // to a name — material kept past its purpose, and unreachable by the next run, which selects
                // on records not yet anonymised.
                errors.AddRange(dependentErrors);

                if (errors.Count > 0)
                {
                    personsFull = false;
                    break;
                }

                // One targeted write per person (#237): the identifying values, the password, the factor and
                // account-email budgets together, and only while the record is still deleted, unanonymised
                // and unrestricted. A record restricted since it was selected is preserved as it stands
                // (NFR-24), and is not counted.
                var anonymisedPersons = new List<Person>();

                foreach (var person in persons)
                {
                    if (await IdentityAnonymiser.AnonymiseAsync(atomicWrites, person, now))
                    {
                        // Counted and named as soon as it is written, so a failure on the next one cannot
                        // drop it from the ledger.
                        anonymisedPersons.Add(person);
                        result.PersonsAnonymised++;
                        anonymisedIds.Add(person.PublicId);
                    }
                }

                // An erased person cannot answer an invitation, and keeping one open would keep naming them
                // (FR-SC-17): their unended invitations are voided, and they are cleared as the inviter of any
                // other. Only for the persons actually anonymised — one restricted since the selection is not
                // erased (NFR-24), so what names them stays as it stands.
                var voided = await ErasedPersonInvitations.ForgetAsync(
                    invitations, auditLog, logger, anonymisedPersons.Select(person => person.Id).ToList(), now);

                result.ScopeOwnerInvitationsVoided += voided.Count;

                personsFull = persons.Count == retention.PurgeBatchSize;

                // A full batch in which nobody could be anonymised made no progress — every one of them
                // changed since it was selected — so it is not repeated: the next run starts afresh.
                if (!personsFull || anonymisedPersons.Count == 0)
                {
                    break;
                }
            }

            // After a failed dependent removal nothing else is attempted this run, as before the drain.
            for (var batch = 0; errors.Count == 0 && batch < retention.MaxBatchesPerTick; batch++)
            {
                var googleUsers = await DueGoogleUsers(now, erasureCutoff, administrativeCutoff)
                    .OrderBy(googleUser => googleUser.DeletedAt)
                    .Take(retention.PurgeBatchSize)
                    .ToListAsync();

                if (googleUsers.Count == 0)
                {
                    googleUsersFull = false;
                    break;
                }

                foreach (var googleUser in googleUsers)
                {
                    IdentityAnonymiser.Anonymise(googleUser, now);
                }

                var saveErrors = (await SaveAsync(googleUsers, googleUserWriter)).ToList();

                // A Google User whose save failed is not counted or named: it was not anonymised.
                if (saveErrors.Count > 0)
                {
                    errors.AddRange(saveErrors);
                    googleUsersFull = false;
                    break;
                }

                result.GoogleUsersAnonymised += googleUsers.Count;
                anonymisedIds.AddRange(googleUsers.Select(googleUser => googleUser.PublicId));

                googleUsersFull = googleUsers.Count == retention.PurgeBatchSize;

                if (!googleUsersFull)
                {
                    break;
                }
            }
        }
        catch (Exception exception)
        {
            interrupted = true;
            logger.LogError(
                exception, "Anonymising the identities due failed part-way; those already anonymised are named");
            errors.Add(RetentionMessages.AnonymisationInterrupted);
        }

        // Outside the catch above: the erasures are done by now, so a count that fails leaves the run as it was
        // and only the backlog unknown. After an interrupted run nothing says where the batches stopped.
        result.Backlog = interrupted
            ? null
            : await CountBacklogAsync(personsFull, googleUsersFull, now, erasureCutoff, administrativeCutoff);

        // The errors ride along with the data rather than replacing it: the identities anonymised before
        // the failure, in this batch or an earlier one, are erased, and the caller has to be able to name
        // them (the erasure ledger, NFR-25) or a restore could not re-apply their erasures.
        result.AnonymisedIds = anonymisedIds;

        var success = output
            .WithData(result)
            .WithMessage(RetentionMessages.ExpiredDeletionsAnonymised);

        return errors.Count > 0
            ? success.WithErrors(errors.Concat(retryErrors).ToList())
            : WithRetryErrors(success, retryErrors);
    }

    /// <summary>
    ///     The identities still due once the run stopped, counted only where it stopped on a full batch — one
    ///     that was not full took all that was due — or <c>null</c> when the count itself failed.
    /// </summary>
    private async Task<int?> CountBacklogAsync(
        bool personsFull, bool googleUsersFull, DateTime now, DateTime erasureCutoff, DateTime administrativeCutoff)
    {
        try
        {
            return (personsFull ? await DuePersons(now, erasureCutoff, administrativeCutoff).CountAsync() : 0)
                   + (googleUsersFull
                       ? await DueGoogleUsers(now, erasureCutoff, administrativeCutoff).CountAsync()
                       : 0);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Counting the identities still due failed; this run's backlog is unknown");

            return null;
        }
    }

    /// <summary>
    ///     The logically deleted persons whose retention window has run out and who are neither
    ///     anonymised nor restricted.
    /// </summary>
    /// <remarks>
    ///     This and <see cref="DueGoogleUsers" /> are near-identical and are still written out twice. A
    ///     generic helper over an interface both entities implement would read better and would not
    ///     translate: EF Core maps the properties of the entity types, and an expression tree
    ///     referencing an interface's members gives it member accesses it cannot resolve to columns.
    ///     The failure mode is a silent client-side evaluation of the whole table, or an exception —
    ///     neither worth the six lines saved.
    /// </remarks>
    private IQueryable<Person> DuePersons(DateTime now, DateTime erasureCutoff, DateTime administrativeCutoff) =>
        personReader.Query()
            // A restricted record is preserved exactly as it stands rather than entering the window
            // (NFR-24): restriction and deletion are independent, so a record can be both — deleted
            // by an administrator while under dispute, or restricted by a subject already pending
            // erasure (UC-44). It is picked up again once the restriction is lifted (UC-45).
            .Where(person => person.IsDeleted
                             && person.AnonymisedAt == null
                             && person.ProcessingRestrictedAt == null
                             && ((person.ErasureDueAt != null && person.ErasureDueAt <= now)
                                 || (person.ErasureDueAt == null
                                     && person.DeletedAt != null
                                     && (person.DeletionKind == SubjectRequested
                                         ? person.DeletedAt <= erasureCutoff
                                         : person.DeletedAt <= administrativeCutoff))));

    /// <summary>The Google User counterpart of <see cref="DuePersons" />.</summary>
    private IQueryable<GoogleUser> DueGoogleUsers(
        DateTime now, DateTime erasureCutoff, DateTime administrativeCutoff) =>
        googleUserReader.Query()
            .Where(googleUser => googleUser.IsDeleted
                                 && googleUser.AnonymisedAt == null
                                 && googleUser.ProcessingRestrictedAt == null
                                 && ((googleUser.ErasureDueAt != null && googleUser.ErasureDueAt <= now)
                                     || (googleUser.ErasureDueAt == null
                                         && googleUser.DeletedAt != null
                                         && (googleUser.DeletionKind == SubjectRequested
                                             ? googleUser.DeletedAt <= erasureCutoff
                                             : googleUser.DeletedAt <= administrativeCutoff))));

    private static DataOutput<AnonymiseExpiredDeletionsCommandOutput?> WithRetryErrors(
        DataOutput<AnonymiseExpiredDeletionsCommandOutput?> output, IReadOnlyCollection<string> retryErrors) =>
        retryErrors.Count == 0 ? output : output.WithErrors(retryErrors.ToList());

    /// <summary>
    ///     Suspends the persons whose erasure request was blocked when it was made and is no longer
    ///     blocked, so the anonymisation can proceed on the next selection.
    /// </summary>
    /// <remarks>
    ///     The suspension is dated from the <em>request</em>, not from the moment the block cleared.
    ///     Dating it from now would restart the retention window and hand back the whole deadline
    ///     for a request that has already been outstanding — a record blocked for two months would
    ///     get another month, which is exactly the breach the deadline exists to prevent. Dating it
    ///     from the request means a long-blocked record is due immediately, which is correct: it is
    ///     already late.
    ///     <para>
    ///         <b>Best-effort and bounded (#210).</b> A failure on one request is logged and counted, and
    ///         the next is still tried; the caller carries the errors on without stopping. Requests are
    ///         loaded <see cref="DataRetentionOptions.PurgeBatchSize" /> at a time, oldest first, so a
    ///         backlog cannot turn one run into one unbounded read.
    ///     </para>
    ///     <para>
    ///         <b>Past the ones still blocked (#206).</b> A request still blocked, or one whose retry fails,
    ///         stays in the selection, so taking the oldest batch again would take the same requests again:
    ///         a batch of them would keep every newer request from ever being retried. Each further batch
    ///         therefore starts after the last request of the one before, by request date and then id, and
    ///         batches are taken while they come back full, up to
    ///         <see cref="DataRetentionOptions.MaxBatchesPerTick" />. No request is tried twice in a run.
    ///     </para>
    ///     <para>
    ///         <b>Both blocks (#203).</b> A request blocked because its subject was a scope's last owner
    ///         (NFR-12) or the last active System Admin (NFR-32) is released once neither holds — whichever
    ///         reason was recorded, both are asked again.
    ///     </para>
    ///     <para>
    ///         <b>Each under the locks of the person's scopes (#202).</b> The guards and the suspension run in
    ///         a transaction of their own per request, holding the rows of the scopes the person owns and the
    ///         System Admin set (<see cref="IInvariantLocks" />, <see cref="LockedPerson" />), as every writer
    ///         that can take an owner or a System Admin out does, so one removed or deleted at the same moment
    ///         is seen.
    ///         A request still blocked commits that transaction too: nothing was written, and committing
    ///         keeps the run's other tracked requests tracked, which a rollback would clear.
    ///     </para>
    /// </remarks>
    /// <returns>The errors to report: empty when every attempted retry went through.</returns>
    private async Task<IReadOnlyCollection<string>> RetryBlockedRequestsAsync(DateTime now)
    {
        var failures = 0;

        try
        {
            DateTime? afterRequestedAt = null;
            var afterId = 0L;

            for (var batch = 0; batch < retention.MaxBatchesPerTick; batch++)
            {
                var candidates = personReader.Query()
                    .Where(person => !person.IsDeleted
                                     && person.ErasureRequestedAt != null
                                     && person.ErasureBlockedReason != null
                                     // NFR-24, as above: a restricted record is not moved toward erasure.
                                     && person.ProcessingRestrictedAt == null);

                if (afterRequestedAt is { } at)
                {
                    candidates = candidates.Where(person =>
                        person.ErasureRequestedAt > at || (person.ErasureRequestedAt == at && person.Id > afterId));
                }

                var blocked = await candidates
                    .OrderBy(person => person.ErasureRequestedAt)
                    .ThenBy(person => person.Id)
                    .Take(retention.PurgeBatchSize)
                    .ToListAsync();

                foreach (var person in blocked)
                {
                    try
                    {
                        await using var transaction = await transactions.BeginAsync();

                        // The person as they stand under the locks (#202, #203): the guards decide on this.
                        var locked = await LockedPerson.LockAsync(locks, personReader, person.Id);

                        // Released once neither NFR-12 nor NFR-32 blocks it, whichever blocked it first (#203).
                        // A targeted write (#237), made only while the request is still blocked and the record
                        // live and unrestricted, so a restriction or deletion that landed since stands.
                        var stillBlocked = locked is null ||
                            await LastScopeOwnerGuard.WouldStripLastOwnerAsync(
                                person.Id, locked.OwnedScopeIds, personReader) ||
                            (locked.IsLiveSystemAdmin &&
                             await LastSystemAdminGuard.WouldLeaveNoneAsync(person.Id, personReader));

                        if (!stillBlocked)
                        {
                            await atomicWrites.TrySuspendBlockedErasureAsync(person, now);
                        }

                        await transaction.CommitAsync();
                    }
                    catch (Exception exception)
                    {
                        failures++;
                        logger.LogError(
                            exception, "Retrying the blocked erasure request of person {PersonId} failed",
                            person.PublicId);
                    }
                }

                if (blocked.Count < retention.PurgeBatchSize)
                {
                    break;
                }

                afterRequestedAt = blocked[^1].ErasureRequestedAt;
                afterId = blocked[^1].Id;
            }
        }
        catch (Exception exception)
        {
            failures++;
            logger.LogError(exception, "Loading the blocked erasure requests to retry failed");
        }

        return failures == 0 ? [] : [RetentionMessages.BlockedErasureRetryFailed];
    }

    /// <summary>
    ///     Forgets, as <see cref="ErasedPersonInvitations" /> does, the persons already anonymised whom an
    ///     unended invitation still names as invitee, or any invitation as inviter — at most a batch per run.
    /// </summary>
    /// <remarks>
    ///     Normally none: the run that anonymised a person forgot them too. One that failed in between
    ///     leaves them anonymised, and the selection takes only persons not yet anonymised, so nothing else
    ///     would come back for them. Both invitation columns are indexed.
    /// </remarks>
    private async Task<IReadOnlyList<Guid>> ForgetAnonymisedStillNamedAsync(DateTime now)
    {
        var anonymised = personReader.Query()
            .Where(person => person.AnonymisedAt != null)
            .Select(person => person.Id);

        var stillNamed = await invitationReader.Query()
            .Where(invitation => invitation.EndedAt == null && anonymised.Contains(invitation.InviteePersonId))
            .Select(invitation => invitation.InviteePersonId)
            .Union(invitationReader.Query()
                .Where(invitation => invitation.InvitedByPersonId != null &&
                                     anonymised.Contains(invitation.InvitedByPersonId.Value))
                .Select(invitation => invitation.InvitedByPersonId!.Value))
            .Take(retention.PurgeBatchSize)
            .ToListAsync();

        return await ErasedPersonInvitations.ForgetAsync(invitations, auditLog, logger, stillNamed, now);
    }

    /// <summary>
    ///     Permanently removes the rows that hold data belonging to the persons being anonymised —
    ///     see <see cref="PersonDependentsRemoval" />, which the re-application of erasures shares.
    /// </summary>
    private async Task<(int Removed, IEnumerable<string> Errors)> RemovePersonDependentsAsync(
        IReadOnlyCollection<Person> persons) =>
        await PersonDependentsRemoval.RemoveAsync(
            persons.Select(person => person.Id).ToList(),
            passwordResetTokenWriter,
            emailVerificationTokenWriter,
            twoFactorAuthWriter);

    private static async Task<IEnumerable<string>> SaveAsync<T>(
        IReadOnlyCollection<T> records, IAsyncRepository<T, long> writer) where T : Entity<long>
    {
        if (records.Count == 0)
        {
            return [];
        }

        var update = await writer.UpdateRangeAsync(records);

        return update.Success ? [] : update.Errors;
    }
}
