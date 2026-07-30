using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.IdentityManager.Command.Input;
using ArturRios.IdentityManager.Command.Output;
using ArturRios.IdentityManager.Domain.Entities;
using ArturRios.IdentityManager.Domain.Enums;
using ArturRios.IdentityManager.Shared.Messages;
using ArturRios.IdentityManager.Shared.Services;
using ArturRios.Mediator.Command.Interfaces;
using ArturRios.Output;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.IdentityManager.Command.Handlers;

/// <summary>
///     Handles <see cref="DeletePersonCommand" /> (UC-09): locates the person (AF-09a), refuses a
///     self-deletion (AF-09d), enforces the per-actor rule (AF-09c), serves an already-deleted person
///     as an idempotent no-op (AF-09b), and otherwise sets <c>IsDeleted = true</c> — provided no scope
///     would be left without an owner (AF-09e, NFR-12). Nothing cascades: the person's join rows,
///     tokens, and owned applications are untouched. All failures are returned as errors on the
///     <see cref="DataOutput{T}" /> rather than thrown.
/// </summary>
public class DeletePersonCommandHandler(
    IAsyncReadOnlyRepository<Person> personReader,
    IAsyncRepository<Person> personWriter,
    IScopeOwnershipChecker scopeOwnership)
    : ICommandHandlerAsync<DeletePersonCommand, DeletePersonCommandOutput>
{
    public async Task<DataOutput<DeletePersonCommandOutput?>> HandleAsync(DeletePersonCommand command)
    {
        var output = DataOutput<DeletePersonCommandOutput?>.New;

        // AF-09a. The lookup deliberately omits an !IsDeleted filter (unlike UC-08) so an
        // already-deleted person is found and served idempotently by AF-09b below. Both scope
        // navigations are needed: ScopeMembership by the authorization rule, ScopeOwnerships by the
        // last-owner guard.
        var person = await personReader.Query()
            .Include(x => x.ScopeMembership)
            .Include(x => x.ScopeOwnerships)
            .FirstOrDefaultAsync(x => x.PublicId == command.Id);

        if (person is null)
        {
            return output.WithError(PersonMessages.PersonNotFound);
        }

        // AF-09d: nobody deletes their own record, System Admin included, so one call cannot lock an
        // administrator out. Checked before authorization, which a System Admin would otherwise pass.
        if (command.ActingPersonId == person.PublicId)
        {
            return output.WithError(PersonMessages.CannotDeleteSelf);
        }

        // UC-09 step 2 (AF-09c). Runs before AF-09b so an already-deleted person cannot be used to
        // probe for the existence of persons outside the actor's scopes.
        if (!await MayDeleteAsync(command, person))
        {
            return output.WithError(PersonMessages.NotAuthorizedToDeletePerson);
        }

        // AF-09b: already deleted, so there is nothing to write. Checked before the last-owner guard —
        // such an owner is already out of the scope, and re-running the guard would turn a required
        // idempotent success into a conflict.
        if (person.IsDeleted)
        {
            return output
                .WithData(new DeletePersonCommandOutput { Id = person.PublicId, AlreadyDeleted = true })
                .WithMessage(PersonMessages.PersonDeletedSuccessfully);
        }

        // AF-09e (NFR-12): a soft-deleted owner can no longer authenticate, so deleting the last one
        // would leave the scope effectively ownerless.
        if (await WouldStripLastOwnerAsync(person))
        {
            return output.WithError(PersonMessages.ScopeWouldLoseLastOwner);
        }

        // UC-09 step 3: flip the flag and stamp UpdatedAt (no DB trigger maintains it).
        person.IsDeleted = true;
        person.UpdatedAt = DateTime.UtcNow;

        var update = await personWriter.UpdateAsync(person);

        if (!update.Success)
        {
            return output.WithErrors(update.Errors);
        }

        // UC-09 step 4.
        return output
            .WithData(new DeletePersonCommandOutput { Id = person.PublicId, AlreadyDeleted = false })
            .WithMessage(PersonMessages.PersonDeletedSuccessfully);
    }

    /// <summary>
    ///     UC-09 step 2. A System Admin may delete any person; a Scope Admin may delete only a
    ///     <c>User</c> belonging to a scope they own. Everything else is denied.
    /// </summary>
    private async Task<bool> MayDeleteAsync(DeletePersonCommand command, Person person)
    {
        if (command.ActingRole == (int)Roles.SystemAdmin)
        {
            return true;
        }

        if (command.ActingRole != (int)Roles.ScopeAdmin ||
            person.RoleId != (long)Roles.User ||
            person.ScopeMembership is null)
        {
            return false;
        }

        return await scopeOwnership.ActorMayManageScopeAsync(
            command.ActingRole, command.ActingPersonId, person.ScopeMembership.ScopeId);
    }

    /// <summary>
    ///     NFR-12. Gathers the scopes somebody *other* than this person owns and reports whether any
    ///     scope this person owns is missing from them — the same guard
    ///     <see cref="UpdatePersonCommandHandler" /> applies to its role change. Persons already
    ///     logically deleted are excluded, since they no longer keep a scope owned.
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
}
