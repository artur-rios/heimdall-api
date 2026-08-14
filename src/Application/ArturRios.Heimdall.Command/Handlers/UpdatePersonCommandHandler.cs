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
    IAsyncReadOnlyRepository<GoogleUser> googleUserReader,
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

        // UC-08 step 5, decided but not yet applied: every refusal below has to leave the loaded
        // person untouched, so the role change is validated first and written further down, once
        // nothing else can reject the request.
        var roleChange = await ValidateRoleChangeAsync(command, person);

        if (roleChange.Error is not null)
        {
            return output.WithError(roleChange.Error);
        }

        // UC-08 step 4: an email change re-checks uniqueness and clears the verification flag.
        var emailChanged = !string.Equals(person.Email, command.Email, StringComparison.OrdinalIgnoreCase);

        // A role change re-checks it too, even on an unchanged address (FR-PE-09). The namespace the
        // address has to be unique in is chosen by the role, so promoting a User to System Admin
        // moves their address out of their scope's space and into the system-wide admin one, where a
        // different set of people may already hold it. Checking only on an email change let that
        // promotion create two live admins sharing an address — after which UC-11's admin lookup
        // (FirstOrDefaultAsync) resolves to one of them and the other can never log in again.
        if ((emailChanged || roleChange.Applies) &&
            await EmailTakenAsync(command, person, roleChange.TargetRoleId))
        {
            return output.WithError(PersonMessages.EmailAlreadyExists);
        }

        if (emailChanged)
        {
            person.EmailVerified = false;
        }

        // FR-PE-10: a System Admin belongs to no scope, so the membership and ownership rows are
        // severed — which deletes them, since both relationships are required and cascade.
        if (roleChange.Applies)
        {
            person.RoleId = roleChange.TargetRoleId;
            person.ScopeMembership = null;
            person.ScopeId = null;
            person.ScopeOwnerships.Clear();
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
    ///     The outcome of examining UC-08's optional role change, before anything is written.
    /// </summary>
    /// <param name="Applies">Whether a role change was requested and is allowed to go ahead.</param>
    /// <param name="TargetRoleId">
    ///     The role the person will hold after this update — their current one when
    ///     <paramref name="Applies" /> is <see langword="false" />. The email-uniqueness rule is
    ///     chosen by this value, not by the role they hold now.
    /// </param>
    /// <param name="Error">The canonical message describing a refusal, or <c>null</c>.</param>
    private sealed record RoleChange(bool Applies, long TargetRoleId, string? Error)
    {
        public static RoleChange None(long currentRoleId) => new(false, currentRoleId, null);

        public static RoleChange Refused(long currentRoleId, string error) =>
            new(false, currentRoleId, error);
    }

    /// <summary>
    ///     UC-08 step 5, as a decision rather than a mutation. Only a change to <c>SystemAdmin</c> is
    ///     supported: every other target role would need a scope the request does not carry
    ///     (FR-PE-02, FR-PE-11).
    /// </summary>
    /// <remarks>
    ///     Nothing is written here, deliberately. The uniqueness check that follows it can still
    ///     refuse the request, and a handler that had already rewritten the loaded person's role
    ///     would be relying on the caller never saving — true today, since a refusal returns before
    ///     <c>UpdateAsync</c>, but true only by accident and invisible from here.
    /// </remarks>
    private async Task<RoleChange> ValidateRoleChangeAsync(UpdatePersonCommand command, Person person)
    {
        if (command.RoleId is null || command.RoleId == (int)person.RoleId)
        {
            return RoleChange.None(person.RoleId);
        }

        // AF-08c: only a System Admin may change a role.
        if (command.ActingRole != (int)Roles.SystemAdmin)
        {
            return RoleChange.Refused(person.RoleId, PersonMessages.RoleChangeRequiresSystemAdmin);
        }

        if (command.RoleId != (int)Roles.SystemAdmin)
        {
            return RoleChange.Refused(person.RoleId, PersonMessages.UnsupportedRoleTransition);
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
                return RoleChange.Refused(person.RoleId, PersonMessages.ScopeWouldLoseLastOwner);
            }
        }

        return new RoleChange(true, (long)Roles.SystemAdmin, null);
    }

    /// <summary>
    ///     FR-PE-09, evaluated against the role the person will have after this update: a
    ///     <c>User</c>'s email is unique within their scope — jointly with that scope's Google Users,
    ///     per FR-GO-07 — and an admin's is unique system-wide. The person being updated is excluded
    ///     so resubmitting their own address is not a conflict. Compared case-insensitively
    ///     (<c>LOWER()</c> in SQL), as UC-06 does.
    /// </summary>
    private async Task<bool> EmailTakenAsync(UpdatePersonCommand command, Person person, long targetRoleId)
    {
        var email = command.Email.ToLower();

        if (targetRoleId == (long)Roles.User && person.ScopeMembership is not null)
        {
            var scopeId = person.ScopeMembership.ScopeId;

            var takenByPerson = await personReader.Query().AnyAsync(other =>
                other.Id != person.Id && !other.IsDeleted && other.Email.ToLower() == email &&
                other.ScopeMembership != null && other.ScopeMembership.ScopeId == scopeId);

            if (takenByPerson)
            {
                return true;
            }

            // The scope's address space is shared with its Google Users (FR-GO-07), so moving a
            // User onto an address a Google User already holds is the same conflict as moving them
            // onto another User's — the same second read CreateUserCommandHandler makes.
            return await googleUserReader.Query().AnyAsync(googleUser =>
                googleUser.ScopeId == scopeId && googleUser.Email.ToLower() == email);
        }

        // An admin belongs to no scope, so no Google User can share their namespace: FR-GO-04 makes
        // every Google account User-equivalent within one scope.
        return await personReader.Query().AnyAsync(other =>
            other.Id != person.Id && !other.IsDeleted && other.Email.ToLower() == email &&
            (other.RoleId == (long)Roles.SystemAdmin || other.RoleId == (long)Roles.ScopeAdmin));
    }
}
