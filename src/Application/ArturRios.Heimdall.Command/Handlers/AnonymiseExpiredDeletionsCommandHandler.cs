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
    ILogger<AnonymiseExpiredDeletionsCommandHandler> logger)
    : ICommandHandlerAsync<AnonymiseExpiredDeletionsCommand, AnonymiseExpiredDeletionsCommandOutput>
{
    /// <summary>
    ///     <c>DeletionKinds.SubjectRequested</c> as a plain <c>int</c>. The comparison happens inside
    ///     an expression EF Core translates to SQL, and a cast of an enum member there is one more
    ///     thing for the provider to have an opinion about than it needs to be.
    /// </summary>
    private const int SubjectRequested = (int)DeletionKinds.SubjectRequested;

    /// <remarks>
    ///     The two queries below are near-identical and are still written out twice. A generic
    ///     helper over an interface both entities implement would read better and would not
    ///     translate: EF Core maps the properties of the entity types, and an expression tree
    ///     referencing an interface's members gives it member accesses it cannot resolve to columns.
    ///     The failure mode is a silent client-side evaluation of the whole table, or an exception —
    ///     neither worth the six lines saved.
    /// </remarks>
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
        await RetryBlockedRequestsAsync(now);

        // Before selecting anyone new: a person anonymised by an earlier run that failed before their
        // invitations were forgotten is never selected again, and would otherwise stay named for good.
        var caughtUp = await ForgetAnonymisedStillNamedAsync(now);

        var persons = await personReader.Query()
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
                                         : person.DeletedAt <= administrativeCutoff))))
            .OrderBy(person => person.DeletedAt)
            .Take(retention.PurgeBatchSize)
            .ToListAsync();

        var googleUsers = await googleUserReader.Query()
            .Where(googleUser => googleUser.IsDeleted
                                 && googleUser.AnonymisedAt == null
                                 && googleUser.ProcessingRestrictedAt == null
                                 && ((googleUser.ErasureDueAt != null && googleUser.ErasureDueAt <= now)
                                     || (googleUser.ErasureDueAt == null
                                         && googleUser.DeletedAt != null
                                         && (googleUser.DeletionKind == SubjectRequested
                                             ? googleUser.DeletedAt <= erasureCutoff
                                             : googleUser.DeletedAt <= administrativeCutoff))))
            .OrderBy(googleUser => googleUser.DeletedAt)
            .Take(retention.PurgeBatchSize)
            .ToListAsync();

        if (persons.Count == 0 && googleUsers.Count == 0)
        {
            return Success(output, 0, 0, 0, invitationsVoided: caughtUp.Count);
        }

        var errors = new List<string>();
        var dependentsRemoved = 0;

        if (persons.Count > 0)
        {
            var (removed, dependentErrors) = await RemovePersonDependentsAsync(persons);

            dependentsRemoved += removed;
            errors.AddRange(dependentErrors);
        }

        // A failed dependent removal stops the run before the record is overwritten. Anonymising
        // anyway would leave the person's two-factor secret behind with nothing left to connect it
        // to a name — material kept past its purpose, and unreachable by the next run, which selects
        // on records not yet anonymised.
        if (errors.Count > 0)
        {
            return output.WithErrors(errors);
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
                anonymisedPersons.Add(person);
            }
        }

        // An erased person cannot answer an invitation, and keeping one open would keep naming them
        // (FR-SC-17): their unended invitations are voided, and they are cleared as the inviter of any
        // other. Only for the persons actually anonymised — one restricted since the selection is not
        // erased (NFR-24), so what names them stays as it stands.
        var voided = await ErasedPersonInvitations.ForgetAsync(
            invitations, auditLog, logger, anonymisedPersons.Select(person => person.Id).ToList(), now);

        foreach (var googleUser in googleUsers)
        {
            IdentityAnonymiser.Anonymise(googleUser, now);
        }

        errors.AddRange(await SaveAsync(googleUsers, googleUserWriter));

        return errors.Count > 0
            ? output.WithErrors(errors)
            : Success(
                output, anonymisedPersons.Count, googleUsers.Count, dependentsRemoved,
                anonymisedPersons.Select(person => person.PublicId)
                    .Concat(googleUsers.Select(googleUser => googleUser.PublicId))
                    .ToList(),
                caughtUp.Count + voided.Count);
    }

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
    /// </remarks>
    private async Task RetryBlockedRequestsAsync(DateTime now)
    {
        var blocked = await personReader.Query()
            .Include(person => person.ScopeOwnerships)
            .Where(person => !person.IsDeleted
                             && person.ErasureRequestedAt != null
                             && person.ErasureBlockedReason != null
                             // NFR-24, as above: a restricted record is not moved toward erasure.
                             && person.ProcessingRestrictedAt == null)
            .ToListAsync();

        foreach (var person in blocked)
        {
            if (await LastScopeOwnerGuard.WouldStripLastOwnerAsync(person, personReader))
            {
                continue;
            }

            // A targeted write (#237), made only while the request is still blocked and the record
            // live and unrestricted, so a restriction or deletion that landed since stands.
            await atomicWrites.TrySuspendBlockedErasureAsync(person, now);
        }
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

    private static DataOutput<AnonymiseExpiredDeletionsCommandOutput?> Success(
        DataOutput<AnonymiseExpiredDeletionsCommandOutput?> output,
        int persons,
        int googleUsers,
        int dependents,
        IEnumerable<Guid>? anonymisedIds = null,
        int invitationsVoided = 0) =>
        output
            .WithData(new AnonymiseExpiredDeletionsCommandOutput
            {
                PersonsAnonymised = persons,
                GoogleUsersAnonymised = googleUsers,
                DependentsRemoved = dependents,
                AnonymisedIds = anonymisedIds ?? [],
                ScopeOwnerInvitationsVoided = invitationsVoided
            })
            .WithMessage(RetentionMessages.ExpiredDeletionsAnonymised);
}
