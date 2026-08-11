using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Heimdall.Shared.Services;
using ArturRios.Mediator.Command.Interfaces;
using ArturRios.Output;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Heimdall.Command.Handlers;

/// <summary>
///     Handles <see cref="UpdatePersonCommand" /> (UC-08): validates the request, loads the person
///     (AF-08a), enforces the per-actor rule, applies an optional role change (AF-08c, NFR-12), then
///     the name and email — re-checking uniqueness per FR-PE-09 and clearing <c>EmailVerified</c>
///     when the address changes (AF-08b). All failures are returned as errors on the output rather
///     than thrown.
/// </summary>
public class UpdatePersonCommandHandler(
    IValidator<UpdatePersonCommand> validator,
    IAsyncReadOnlyRepository<Person> personReader,
    IAsyncRepository<Person> personWriter,
    IScopeOwnershipChecker scopeOwnership)
    : ICommandHandlerAsync<UpdatePersonCommand, UpdatePersonCommandOutput>
{
    public async Task<DataOutput<UpdatePersonCommandOutput?>> HandleAsync(UpdatePersonCommand command)
    {
        var output = DataOutput<UpdatePersonCommandOutput?>.New;

        // Step 2: validate input shape.
        var validation = await validator.ValidateAsync(command);

        if (!validation.IsValid)
        {
            return output.WithErrors(validation.Errors.Select(failure => failure.ErrorMessage));
        }

        // AF-08a: the person must exist and not be logically deleted. Both scope navigations are
        // included because severing them is how the join rows get deleted (see the design doc), and
        // each join row's Scope is included too because the response reports scope PublicIds.
        var person = await personReader.Query()
            .Include(x => x.ScopeMembership)
            .ThenInclude(membership => membership!.Scope)
            .Include(x => x.ScopeOwnerships)
            .ThenInclude(ownership => ownership.Scope)
            .FirstOrDefaultAsync(x => x.PublicId == command.Id && !x.IsDeleted);

        if (person is null)
        {
            return output.WithError(PersonMessages.PersonNotFound);
        }

        // UC-08 step 3: a System Admin may update anyone; anyone may update themselves; a Scope Admin
        // may update a User of a scope they own.
        if (!await MayUpdateAsync(command, person))
        {
            return output.WithError(PersonMessages.NotAuthorizedToUpdatePerson);
        }

        // UC-08 step 5: apply the role change, if one was asked for.
        var roleChange = await ApplyRoleChangeAsync(command, person);

        if (roleChange is not null)
        {
            return output.WithError(roleChange);
        }

        // UC-08 step 4: an email change re-checks uniqueness and clears the verification flag.
        var emailChanged = !string.Equals(person.Email, command.Email, StringComparison.OrdinalIgnoreCase);

        if (emailChanged)
        {
            if (await EmailTakenAsync(command, person))
            {
                return output.WithError(PersonMessages.EmailAlreadyExists);
            }

            person.EmailVerified = false;
        }

        // UC-08 step 6: apply and stamp UpdatedAt (no DB trigger maintains it).
        person.Name = command.Name;
        person.Email = command.Email;
        person.UpdatedAt = DateTime.UtcNow;

        var update = await personWriter.UpdateAsync(person);

        if (!update.Success)
        {
            return output.WithErrors(update.Errors);
        }

        // UC-08 step 7: return the updated person.
        return output
            .WithData(new UpdatePersonCommandOutput
            {
                Id = person.PublicId,
                Name = person.Name,
                Email = person.Email,
                Role = (int)person.RoleId,
                EmailVerified = person.EmailVerified,
                ScopeId = person.ScopeMembership?.Scope.PublicId,
                OwnedScopeIds = person.ScopeOwnerships.Select(ownership => ownership.Scope.PublicId).ToList(),
                CreatedAt = person.CreatedAt,
                UpdatedAt = person.UpdatedAt
            })
            .WithMessage(PersonMessages.PersonUpdatedSuccessfully);
    }

    /// <summary>
    ///     UC-08 step 3. A System Admin may update any person; any actor may update their own record;
    ///     a Scope Admin may update a <c>User</c> belonging to a scope they own. Everything else is
    ///     denied.
    /// </summary>
    private async Task<bool> MayUpdateAsync(UpdatePersonCommand command, Person person)
    {
        if (command.ActingRole == (int)Roles.SystemAdmin || command.ActingPersonId == person.PublicId)
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
    ///     UC-08 step 5. Returns <c>null</c> when there is nothing to do or the change was applied,
    ///     or the canonical message describing why the change was refused.
    ///     Only a change to <c>SystemAdmin</c> is supported: every other target role would need a
    ///     scope the request does not carry (FR-PE-02, FR-PE-11). A person becoming a System Admin
    ///     must end up with no scope association at all (FR-PE-10), so their membership and ownership
    ///     rows are severed — which deletes them, since both relationships are required and cascade.
    /// </summary>
    private async Task<string?> ApplyRoleChangeAsync(UpdatePersonCommand command, Person person)
    {
        if (command.RoleId is null || command.RoleId == (int)person.RoleId)
        {
            return null;
        }

        // AF-08c: only a System Admin may change a role.
        if (command.ActingRole != (int)Roles.SystemAdmin)
        {
            return PersonMessages.RoleChangeRequiresSystemAdmin;
        }

        if (command.RoleId != (int)Roles.SystemAdmin)
        {
            return PersonMessages.UnsupportedRoleTransition;
        }

        // NFR-12: a scope must always retain at least one owner. Gather the scopes somebody *other*
        // than this person owns; refuse if any scope this person owns is not among them. Persons
        // already logically deleted are excluded, since they can no longer authenticate and so do
        // not keep a scope owned — the same guard UC-09 and UC-10 apply.
        if (person.RoleId == (long)Roles.ScopeAdmin && person.ScopeOwnerships.Count > 0)
        {
            var ownedScopeIds = person.ScopeOwnerships.Select(ownership => ownership.ScopeId).ToList();

            var coOwnedScopeIds = await personReader.Query()
                .Where(other => other.Id != person.Id && !other.IsDeleted)
                .SelectMany(other => other.ScopeOwnerships.Select(ownership => ownership.ScopeId))
                .Distinct()
                .ToListAsync();

            if (ownedScopeIds.Any(scopeId => !coOwnedScopeIds.Contains(scopeId)))
            {
                return PersonMessages.ScopeWouldLoseLastOwner;
            }
        }

        person.RoleId = (long)Roles.SystemAdmin;
        person.ScopeMembership = null;
        person.ScopeOwnerships.Clear();

        return null;
    }

    /// <summary>
    ///     FR-PE-09, evaluated against the role the person will have after this update: a
    ///     <c>User</c>'s email is unique within their scope, an admin's is unique system-wide. The
    ///     person being updated is excluded so resubmitting their own address is not a conflict.
    ///     Compared case-insensitively (<c>LOWER()</c> in SQL), as UC-06 does.
    /// </summary>
    private async Task<bool> EmailTakenAsync(UpdatePersonCommand command, Person person)
    {
        var email = command.Email.ToLower();

        if (person.RoleId == (long)Roles.User && person.ScopeMembership is not null)
        {
            var scopeId = person.ScopeMembership.ScopeId;

            return await personReader.Query().AnyAsync(other =>
                other.Id != person.Id && !other.IsDeleted && other.Email.ToLower() == email &&
                other.ScopeMembership != null && other.ScopeMembership.ScopeId == scopeId);
        }

        return await personReader.Query().AnyAsync(other =>
            other.Id != person.Id && !other.IsDeleted && other.Email.ToLower() == email &&
            (other.RoleId == (long)Roles.SystemAdmin || other.RoleId == (long)Roles.ScopeAdmin));
    }
}
