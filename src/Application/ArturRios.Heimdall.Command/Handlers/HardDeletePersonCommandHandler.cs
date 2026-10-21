using ArturRios.Data.Relational.Core.Entities;
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
///     Handles <see cref="HardDeletePersonCommand" /> (UC-10): locates the person in any deletion state
///     (AF-10a), refuses a self-deletion (AF-10c), refuses to strip a scope of its last owner (AF-10b,
///     NFR-12) or the system of its last active System Admin (AF-10d, NFR-32), then permanently deletes
///     the applications the person owns (NFR-11) and their password reset and email verification
///     tokens, and finally the person — whose <c>ON DELETE CASCADE</c>
///     foreign keys remove the <c>SCOPE_USER</c>/<c>SCOPE_OWNER</c> join rows. The response reports the
///     totals of the removed dependents, counted regardless of their individual deletion state. All
///     failures are returned as errors on the <see cref="DataOutput{T}" /> rather than thrown.
/// </summary>
/// <remarks>
///     <para>
///         Before anything is removed, the person's <c>PublicId</c> is tombstoned
///         (<see cref="ErasedSubject" />, NFR-21, #195). Audit entries name their target by that bare id,
///         so once the row is gone the tombstone is the only evidence the retention pass and the database
///         have that those entries — this hard delete's own among them — name a person whose target must
///         be cleared. A retry writes it again without error.
///     </para>
///     <para>
///         <b>All or nothing</b> (#212). The tombstone, the dependents and the person are removed in one
///         <see cref="IWriteTransactions" /> transaction, committed only once the person is gone, so a
///         failure at any step leaves the person with every application and token they had, and no
///         tombstone for a live row.
///     </para>
///     <para>
///         <b>Under the locks of the person's scopes</b> (#202), and of the System Admin set when the
///         person is a live one (#203). The transaction opens before the guards and first locks the rows of
///         the scopes the person owns, then the System Admin set (<see cref="IInvariantLocks" />), as every
///         writer that can take an owner or a System Admin out does, so the guards' answers still hold when
///         the person is deleted.
///     </para>
/// </remarks>
public class HardDeletePersonCommandHandler(
    IAsyncReadOnlyRepository<Person, long> personReader,
    IAsyncRepository<Person, long> personWriter,
    IAsyncReadOnlyRepository<Application, long> applicationReader,
    IAsyncRepository<Application, long> applicationWriter,
    IAsyncReadOnlyRepository<PasswordResetToken, long> passwordResetTokenReader,
    IAsyncRepository<PasswordResetToken, long> passwordResetTokenWriter,
    IAsyncReadOnlyRepository<EmailVerificationToken, long> emailVerificationTokenReader,
    IAsyncRepository<EmailVerificationToken, long> emailVerificationTokenWriter,
    IErasedSubjects erasedSubjects,
    IWriteTransactions transactions,
    IInvariantLocks locks)
    : ICommandHandlerAsync<HardDeletePersonCommand, HardDeletePersonCommandOutput>
{
    public async Task<DataOutput<HardDeletePersonCommandOutput?>> HandleAsync(HardDeletePersonCommand command, CancellationToken cancellationToken = default)
    {
        var output = DataOutput<HardDeletePersonCommandOutput?>.New;

        // AF-10a: the lookup omits an !IsDeleted filter — a logically deleted person is exactly what a
        // cleanup pass starts from, so it must still be hard-deletable.
        var person = await personReader.Query()
            .FirstOrDefaultAsync(x => x.PublicId == command.Id);

        if (person is null)
        {
            return output.WithError(PersonMessages.PersonNotFound);
        }

        // AF-10c: nobody hard-deletes their own record, System Admin included, so one call cannot
        // permanently destroy the caller's own account. Checked before the last-owner guard, so a
        // caller targeting themselves gets the reason that applies to them.
        if (command.ActingPersonId == person.PublicId)
        {
            return output.WithError(PersonMessages.CannotDeleteSelf);
        }

        // UC-10 steps 3-8 land together or not at all (#212): leaving this block by any return or
        // exception before the commit rolls every one of them back.
        await using var transaction = await transactions.BeginAsync();

        // The person's scopes, read and locked first (#202), so the guard below is still true when the
        // person is deleted.
        var systemAdmin = LastSystemAdminGuard.Applies(person);
        var ownedScopeIds =
            await LastScopeOwnerGuard.LockOwnedScopesAsync(locks, personReader, person.Id, systemAdmin);

        // UC-10 step 3 (AF-10b, NFR-12). Unlike UC-09, this runs regardless of the person's own
        // deletion state: NFR-12 names hard-deleting the last owning person explicitly, and the guard
        // keeps every scope row backed by at least one SCOPE_OWNER row.
        if (await LastScopeOwnerGuard.WouldStripLastOwnerAsync(person.Id, ownedScopeIds, personReader))
        {
            return output.WithError(PersonMessages.ScopeWouldLoseLastOwner);
        }

        // AF-10d (NFR-32): nor may it leave the system without an active System Admin. Only a live one is
        // guarded: a logically deleted administrator is no longer one of the set.
        if (systemAdmin && await LastSystemAdminGuard.WouldLeaveNoneAsync(person.Id, personReader))
        {
            return output.WithError(PersonMessages.WouldRemoveLastSystemAdmin);
        }

        // UC-10 step 4 (NFR-21, #195): tombstone the id before anything irreversible happens, so the
        // trail's entries about this person can still be cleared once the row that said who they
        // were is gone.
        await erasedSubjects.RecordAsync(
        [
            new ErasedSubject
            {
                Id = person.PublicId,
                Kind = (int)ErasedSubjectKinds.Person,
                CreatedAt = DateTime.UtcNow
            }
        ]);

        // UC-10 steps 5-6: the dependents, counted regardless of individual deletion state.
        var applications = await applicationReader.Query()
            .Where(a => a.OwnerId == person.Id)
            .ToListAsync();
        var passwordResetTokens = await passwordResetTokenReader.Query()
            .Where(t => t.PersonId == person.Id)
            .ToListAsync();
        var emailVerificationTokens = await emailVerificationTokenReader.Query()
            .Where(t => t.PersonId == person.Id)
            .ToListAsync();

        // Applications and tokens reference the person, so they go first and no foreign key is ever
        // violated.
        var deleteErrors = (await DeleteAllAsync(applications, applicationWriter))
            .Concat(await DeleteAllAsync(passwordResetTokens, passwordResetTokenWriter))
            .Concat(await DeleteAllAsync(emailVerificationTokens, emailVerificationTokenWriter))
            .ToList();

        if (deleteErrors.Count > 0)
        {
            return output.WithErrors(deleteErrors);
        }

        // UC-10 steps 7-8: delete the person; its ON DELETE CASCADE foreign keys clear the SCOPE_USER
        // or SCOPE_OWNER join rows.
        var personDelete = await personWriter.DeleteAsync(person);

        if (!personDelete.Success)
        {
            return output.WithErrors(personDelete.Errors);
        }

        await transaction.CommitAsync();

        // UC-10 step 9.
        return output
            .WithData(new HardDeletePersonCommandOutput
            {
                Id = person.PublicId,
                DeletedApplicationCount = applications.Count,
                DeletedTokenCount = passwordResetTokens.Count + emailVerificationTokens.Count
            })
            .WithMessage(PersonMessages.PersonHardDeletedSuccessfully);
    }

    /// <summary>
    ///     Permanently removes every entity in <paramref name="dependents" /> by internal Id, or does
    ///     nothing when the set is empty. Returns any persistence errors, or an empty sequence on
    ///     success / no-op.
    /// </summary>
    private static async Task<IEnumerable<string>> DeleteAllAsync<T>(
        IReadOnlyCollection<T> dependents,
        IAsyncRepository<T, long> writer) where T : Entity<long>
    {
        if (dependents.Count == 0)
        {
            return [];
        }

        var result = await writer.DeleteRangeAsync(dependents.Select(dependent => dependent.Id));

        return result.Success ? [] : result.Errors;
    }
}
