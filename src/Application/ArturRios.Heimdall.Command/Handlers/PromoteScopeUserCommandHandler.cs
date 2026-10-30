using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Data.Relational.Core.Repositories;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Domain.Persistence;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Heimdall.Shared.Services;
using ArturRios.Mediator.Command.Interfaces;
using ArturRios.Output;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Heimdall.Command.Handlers;

/// <summary>
///     Handles <see cref="PromoteScopeUserCommand" /> (UC-23, FR-SC-08/FR-SC-13/FR-RO-03): verifies
///     the target scope exists and is active (AF-23a), enforces scope ownership for a Scope Admin
///     actor (AF-23c), refuses a person who already holds <c>ScopeAdmin</c> (AF-23d), verifies the
///     named person is a non-deleted <c>User</c> of that scope (AF-23b), then promotes them — role to
///     <c>ScopeAdmin</c>, <c>SCOPE_USER</c> row removed, <c>SCOPE_OWNER</c> row added — in one transaction,
///     so FR-PE-11 is never observably violated. A System Admin actor bypasses the ownership check.
///     All failures are returned as errors on the <see cref="DataOutput{T}" /> rather than thrown.
/// </summary>
public class PromoteScopeUserCommandHandler(
    IAsyncReadOnlyRepository<Scope, long> scopeReader,
    IAsyncReadOnlyRepository<Person, long> personReader,
    IScopeOwnershipChecker scopeOwnership,
    IAtomicWrites atomicWrites)
    : ICommandHandlerAsync<PromoteScopeUserCommand, PromoteScopeUserCommandOutput>
{
    public async Task<DataOutput<PromoteScopeUserCommandOutput?>> HandleAsync(PromoteScopeUserCommand command, CancellationToken cancellationToken = default)
    {
        var output = DataOutput<PromoteScopeUserCommandOutput?>.New;

        // AF-23a (UC-23 step 2): the target scope must exist and not be logically deleted — the
        // alternative flow names both conditions as one outcome, so the filter answers for both.
        var scope = await scopeReader.Query()
            .FirstOrDefaultAsync(x => x.PublicId == command.ScopeId && !x.IsDeleted);

        if (scope is null)
        {
            return output.WithError(PersonMessages.ScopeNotFound);
        }

        // AF-23c: a Scope Admin actor may only act on a scope they own; a System Admin bypasses.
        // Checked before the person is looked up so a caller who fails here learns nothing about
        // which person ids exist, what roles they hold, or which scope they belong to.
        if (!await scopeOwnership.ActorMayManageScopeAsync(command.ActingRole, command.ActingPersonId, scope.Id))
        {
            return output.WithError(PersonMessages.NotScopeOwner);
        }

        // The person must exist and not be logically deleted (AF-23b): a deleted person can no longer
        // authenticate, so the ownership granted could never be exercised. The membership is included
        // because AF-23b reads it.
        var person = await personReader.Query()
            .Include(x => x.ScopeMembership)
            .FirstOrDefaultAsync(x => x.PublicId == command.PersonId && !x.IsDeleted);

        if (person is null)
        {
            return output.WithError(PersonMessages.PersonNotScopeUser);
        }

        // AF-23d: a person who already holds ScopeAdmin has nothing to be promoted to. Checked before
        // AF-23b because such a person also satisfies "not a User of that scope" — the more specific
        // flow has to win, or AF-23d would be unreachable.
        if (person.RoleId == (long)Roles.ScopeAdmin)
        {
            return output.WithError(PersonMessages.AlreadyScopeAdmin);
        }

        // AF-23b (UC-23 step 3): the person must be a User *of this scope*. An unknown id, a User of
        // another scope, and a System Admin all answer alike, so the endpoint cannot be used to probe
        // which persons exist or where they belong.
        if (person.RoleId != (long)Roles.User ||
            person.ScopeMembership is null ||
            person.ScopeMembership.ScopeId != scope.Id)
        {
            return output.WithError(PersonMessages.PersonNotScopeUser);
        }

        // FR-PE-09: the promotion moves the address from the scope's User namespace into the
        // system-wide admin namespace, where it must be unique. Compared case-insensitively
        // (LOWER() in SQL) and ignoring logically deleted admins, as UC-06 path c does.
        var email = person.Email.ToLower();

        var emailTaken = await personReader.Query().AnyAsync(other =>
            other.Id != person.Id && !other.IsDeleted && other.Email.ToLower() == email &&
            (other.RoleId == (long)Roles.SystemAdmin || other.RoleId == (long)Roles.ScopeAdmin));

        if (emailTaken)
        {
            return output.WithError(PersonMessages.EmailAlreadyExists);
        }

        // UC-23 steps 4-5, together, and both targeted (#237, #205): the role — with ScopeId, the
        // membership's copy, and the SCOPE_USER row removed beside it — made only while the person is
        // still the User they were read as, so a promotion, deletion or role change that landed since is
        // never overwritten; then, in the same transaction, the SCOPE_OWNER row, as UC-21 writes it. Not
        // a save of the person aggregate, which would write the profile back as read here, over a rename
        // made meanwhile. UpdatedAt is stamped by hand — no trigger maintains it.
        var change = await atomicWrites.TryChangeEmailAndRoleAsync(
            person, person.Email, (long)Roles.ScopeAdmin, DateTime.UtcNow,
            () => atomicWrites.TryAddScopeOwnerAsync(person, scope.Id));

        switch (change)
        {
            // Not expected — the change has just made them a live Scope Admin in the same transaction —
            // but a refused ownership means the person is not as read, which a stale change answers.
            case AccountChangeOutcome.SaveFailed:
            case AccountChangeOutcome.Stale:
                return output.WithError(RelationalErrors.ConcurrencyMessage);
            case AccountChangeOutcome.AddressTaken:
                return output.WithError(RelationalErrors.UniqueViolationMessage);
        }

        // UC-23 step 6: return the updated person. The scope just granted is the whole of what they
        // own — they were a User, and a User owns no scope (FR-SC-08, FR-PE-11).
        return output
            .WithData(new PromoteScopeUserCommandOutput
            {
                Id = person.PublicId,
                Name = person.Name,
                Email = person.Email,
                Role = (int)Roles.ScopeAdmin,
                EmailVerified = person.EmailVerified,
                OwnedScopeIds = [scope.PublicId],
                CreatedAt = person.CreatedAt,
                UpdatedAt = person.UpdatedAt
            })
            .WithMessage(PersonMessages.ScopeUserPromotedSuccessfully);
    }
}
