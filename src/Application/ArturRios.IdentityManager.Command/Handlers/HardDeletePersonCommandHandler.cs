using ArturRios.Data.Relational.Core.Entities;
using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.IdentityManager.Command.Input;
using ArturRios.IdentityManager.Command.Output;
using ArturRios.IdentityManager.Domain.Entities;
using ArturRios.IdentityManager.Domain.Enums;
using ArturRios.IdentityManager.Shared.Messages;
using ArturRios.Mediator.Command.Interfaces;
using ArturRios.Output;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.IdentityManager.Command.Handlers;

/// <summary>
///     Handles <see cref="HardDeletePersonCommand" /> (UC-10): locates the person in any deletion state
///     (AF-10a), refuses a self-deletion (AF-10c), refuses to strip a scope of its last owner (AF-10b,
///     NFR-12), then permanently deletes the applications the person owns (NFR-11) and their password
///     reset and email verification tokens, and finally the person — whose <c>ON DELETE CASCADE</c>
///     foreign keys remove the <c>SCOPE_USER</c>/<c>SCOPE_OWNER</c> join rows. The response reports the
///     totals of the removed dependents, counted regardless of their individual deletion state. All
///     failures are returned as errors on the <see cref="DataOutput{T}" /> rather than thrown.
/// </summary>
public class HardDeletePersonCommandHandler(
    IAsyncReadOnlyRepository<Person> personReader,
    IAsyncRepository<Person> personWriter,
    IAsyncReadOnlyRepository<Application> applicationReader,
    IAsyncRepository<Application> applicationWriter,
    IAsyncReadOnlyRepository<PasswordResetToken> passwordResetTokenReader,
    IAsyncRepository<PasswordResetToken> passwordResetTokenWriter,
    IAsyncReadOnlyRepository<EmailVerificationToken> emailVerificationTokenReader,
    IAsyncRepository<EmailVerificationToken> emailVerificationTokenWriter)
    : ICommandHandlerAsync<HardDeletePersonCommand, HardDeletePersonCommandOutput>
{
    public async Task<DataOutput<HardDeletePersonCommandOutput?>> HandleAsync(HardDeletePersonCommand command)
    {
        var output = DataOutput<HardDeletePersonCommandOutput?>.New;

        // AF-10a: the lookup omits an !IsDeleted filter — a logically deleted person is exactly what a
        // cleanup pass starts from, so it must still be hard-deletable. ScopeOwnerships is needed by
        // the last-owner guard below.
        var person = await personReader.Query()
            .Include(x => x.ScopeOwnerships)
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

        // UC-10 step 2 (AF-10b, NFR-12). Unlike UC-09, this runs regardless of the person's own
        // deletion state: NFR-12 names hard-deleting the last owning person explicitly, and the guard
        // keeps every scope row backed by at least one SCOPE_OWNER row.
        if (await WouldStripLastOwnerAsync(person))
        {
            return output.WithError(PersonMessages.ScopeWouldLoseLastOwner);
        }

        // UC-10 steps 3-4: the dependents, counted regardless of individual deletion state.
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

        // UC-10 steps 5-6: delete the person; its ON DELETE CASCADE foreign keys clear the SCOPE_USER
        // or SCOPE_OWNER join rows.
        var personDelete = await personWriter.DeleteAsync(person);

        if (!personDelete.Success)
        {
            return output.WithErrors(personDelete.Errors);
        }

        // UC-10 step 7.
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
    ///     NFR-12. Gathers the scopes somebody *other* than this person owns and reports whether any
    ///     scope this person owns is missing from them — the same guard
    ///     <see cref="DeletePersonCommandHandler" /> and <see cref="UpdatePersonCommandHandler" />
    ///     apply. Persons already logically deleted are excluded, since they no longer keep a scope
    ///     owned.
    /// </summary>
    private async Task<bool> WouldStripLastOwnerAsync(Person person)
    {
        if (person.RoleId != (long)Roles.ScopeAdmin || person.ScopeOwnerships.Count == 0)
        {
            return false;
        }

        var ownedScopeIds = person.ScopeOwnerships.Select(ownership => ownership.ScopeId).ToList();

        var coOwnedScopeIds = await personReader.Query()
            .Where(other => other.Id != person.Id && !other.IsDeleted)
            .SelectMany(other => other.ScopeOwnerships.Select(ownership => ownership.ScopeId))
            .Distinct()
            .ToListAsync();

        return ownedScopeIds.Any(scopeId => !coOwnedScopeIds.Contains(scopeId));
    }

    /// <summary>
    ///     Permanently removes every entity in <paramref name="dependents" /> by internal Id, or does
    ///     nothing when the set is empty. Returns any persistence errors, or an empty sequence on
    ///     success / no-op.
    /// </summary>
    private static async Task<IEnumerable<string>> DeleteAllAsync<T>(
        IReadOnlyCollection<T> dependents,
        IAsyncRepository<T> writer) where T : Entity
    {
        if (dependents.Count == 0)
        {
            return [];
        }

        var result = await writer.DeleteRangeAsync(dependents.Select(dependent => dependent.Id));

        return result.Success ? [] : result.Errors;
    }
}
