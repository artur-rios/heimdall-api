using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Command.Services;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Domain.Persistence;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Mediator.Command.Interfaces;
using ArturRios.Output;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Heimdall.Command.Handlers;

/// <summary>
///     Handles <see cref="ReapplyErasuresCommand" /> (NFR-25): anonymises identities named by the
///     erasure ledger, for use after a database restore. All failures are returned as errors on the
///     <see cref="DataOutput{T}" /> rather than thrown.
/// </summary>
/// <remarks>
///     <para>
///         <b>It anonymises immediately, not on a deadline.</b> Every identity named here was
///         already erased once; the deadline that governed it has passed, and starting a fresh
///         window would hand back time on an obligation that was discharged before the restore.
///     </para>
///     <para>
///         <b>An unknown identifier is not an error.</b> A ledger covers every erasure ever
///         completed, while a restore reinstates only what one backup held, so most of the list will
///         match nothing on any given run. Treating that as failure would make the reconciliation
///         look broken every time it worked.
///     </para>
///     <para>
///         <b>It is deliberately usable more than once.</b> A restore is a stressful operation and
///         the runbook step may be repeated or half-completed; running it twice anonymises nothing
///         extra and reports the difference.
///     </para>
/// </remarks>
public class ReapplyErasuresCommandHandler(
    IAsyncReadOnlyRepository<Person, long> personReader,
    IAsyncReadOnlyRepository<GoogleUser, long> googleUserReader,
    IAsyncRepository<GoogleUser, long> googleUserWriter,
    IAsyncRepository<PasswordResetToken, long> passwordResetTokenWriter,
    IAsyncRepository<EmailVerificationToken, long> emailVerificationTokenWriter,
    IAsyncRepository<TwoFactorAuth, long> twoFactorAuthWriter,
    IAtomicWrites atomicWrites,
    IScopeOwnerInvitations invitations)
    : ICommandHandlerAsync<ReapplyErasuresCommand, ReapplyErasuresCommandOutput>
{
    public async Task<DataOutput<ReapplyErasuresCommandOutput?>> HandleAsync(ReapplyErasuresCommand command, CancellationToken cancellationToken = default)
    {
        var output = DataOutput<ReapplyErasuresCommandOutput?>.New;
        var requested = command.SubjectIds.Distinct().ToList();

        if (requested.Count == 0)
        {
            return output.WithError(ErasureMessages.NoSubjectsToReapply);
        }

        var now = DateTime.UtcNow;
        var anonymised = 0;
        var alreadyAnonymised = 0;

        var persons = await personReader.Query()
            .Where(person => requested.Contains(person.PublicId))
            .ToListAsync();

        var googleUsers = await googleUserReader.Query()
            .Where(googleUser => requested.Contains(googleUser.PublicId))
            .ToListAsync();

        var toWrite = new List<Person>();

        foreach (var person in persons)
        {
            if (person.AnonymisedAt is not null)
            {
                alreadyAnonymised++;

                continue;
            }

            toWrite.Add(person);
        }

        var googleUsersToWrite = new List<GoogleUser>();

        foreach (var googleUser in googleUsers)
        {
            if (googleUser.AnonymisedAt is not null)
            {
                alreadyAnonymised++;

                continue;
            }

            googleUser.IsDeleted = true;
            googleUser.DeletedAt = googleUser.DeletedAt ?? now;
            googleUser.DeletionKind = (int)DeletionKinds.SubjectRequested;

            IdentityAnonymiser.Anonymise(googleUser, now);

            googleUsersToWrite.Add(googleUser);
            anonymised++;
        }

        // The restored copy brought back what the original erasure removed alongside the identity —
        // the two-factor secret with its codes, and any reset or verification tokens. Removed before
        // the identity is overwritten, as the scheduled pass does, so a failure here leaves the
        // record selectable for another attempt rather than anonymised with its secret kept.
        var (_, dependentErrors) = await PersonDependentsRemoval.RemoveAsync(
            toWrite.Select(person => person.Id).ToList(),
            passwordResetTokenWriter,
            emailVerificationTokenWriter,
            twoFactorAuthWriter);

        if (dependentErrors.Count > 0)
        {
            return output.WithErrors(dependentErrors);
        }

        // As the scheduled pass does: the restored copy may have brought back invitations the erasure
        // voided, and the person as the inviter of others (FR-SC-17). Before the identity is written,
        // like the dependents, so a failure leaves the person selectable for another attempt; every
        // person here ends anonymised, by this run or by one that got there first.
        IReadOnlyList<Guid> voided = toWrite.Count == 0
            ? []
            : await invitations.ForgetPersonsAsync(toWrite.Select(person => person.Id).ToList(), now);

        // Then each person, by one targeted write (#237): marked as a subject-requested deletion, so
        // the record's own state says why it is gone and the audit trail reads correctly afterwards,
        // and anonymised with its password and budgets in the same statement. One anonymised since it
        // was read is counted as such.
        foreach (var person in toWrite)
        {
            if (await IdentityAnonymiser.ReapplyAsync(atomicWrites, person, now))
            {
                anonymised++;
            }
            else
            {
                alreadyAnonymised++;
            }
        }

        var errors = new List<string>();

        if (googleUsersToWrite.Count > 0)
        {
            var update = await googleUserWriter.UpdateRangeAsync(googleUsersToWrite);

            if (!update.Success)
            {
                errors.AddRange(update.Errors);
            }
        }

        if (errors.Count > 0)
        {
            return output.WithErrors(errors);
        }

        var found = persons.Select(person => person.PublicId)
            .Concat(googleUsers.Select(googleUser => googleUser.PublicId))
            .ToHashSet();

        return output
            .WithData(new ReapplyErasuresCommandOutput
            {
                Anonymised = anonymised,
                AlreadyAnonymised = alreadyAnonymised,
                NotFound = requested.Where(id => !found.Contains(id)).ToList(),
                VoidedScopeOwnerInvitationIds = voided
            })
            .WithMessage(ErasureMessages.ErasuresReapplied);
    }
}
