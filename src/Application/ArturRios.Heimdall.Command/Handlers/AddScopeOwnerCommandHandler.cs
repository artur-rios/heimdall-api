using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Heimdall.Shared.Services;
using ArturRios.Mediator.Command.Interfaces;
using ArturRios.Output;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Heimdall.Command.Handlers;

/// <summary>
///     Handles <see cref="AddScopeOwnerCommand" /> (UC-21, FR-SC-08/FR-SC-09): verifies the target
///     scope exists and is active (AF-21a), enforces scope ownership for a Scope Admin actor
///     (AF-21c), verifies the named person is an existing, non-deleted <c>ScopeAdmin</c> (AF-21b),
///     then links them to the scope with a <c>SCOPE_OWNER</c> row — serving a person who already owns
///     it as an idempotent no-op (AF-21d). A System Admin actor bypasses the ownership check. All
///     failures are returned as errors on the <see cref="DataOutput{T}" /> rather than thrown.
/// </summary>
public class AddScopeOwnerCommandHandler(
    IAsyncReadOnlyRepository<Scope> scopeReader,
    IAsyncReadOnlyRepository<Person> personReader,
    IAsyncRepository<Person> personWriter,
    IScopeOwnershipChecker scopeOwnership)
    : ICommandHandlerAsync<AddScopeOwnerCommand, AddScopeOwnerCommandOutput>
{
    public async Task<DataOutput<AddScopeOwnerCommandOutput?>> HandleAsync(AddScopeOwnerCommand command)
    {
        var output = DataOutput<AddScopeOwnerCommandOutput?>.New;

        // AF-21a (UC-21 step 2): the target scope must exist and not be logically deleted — the
        // alternative flow names both conditions as one outcome, so the filter answers for both.
        var scope = await scopeReader.Query()
            .FirstOrDefaultAsync(x => x.PublicId == command.ScopeId && !x.IsDeleted);

        if (scope is null)
        {
            return output.WithError(PersonMessages.ScopeNotFound);
        }

        // AF-21c: a Scope Admin actor may only act on a scope they own; a System Admin bypasses.
        // Checked before the person is looked up so a caller who fails here learns nothing about
        // which person ids exist or what roles they hold.
        if (!await scopeOwnership.ActorMayManageScopeAsync(command.ActingRole, command.ActingPersonId, scope.Id))
        {
            return output.WithError(PersonMessages.NotScopeOwner);
        }

        // AF-21b (UC-21 step 3): the person must exist, not be logically deleted, and hold the
        // ScopeAdmin role (FR-SC-08). A deleted person can no longer authenticate, so an ownership
        // granted to them could never be exercised. All three conditions share one answer, so the
        // endpoint cannot be used to tell an unknown id from a User. The ownership rows are included
        // because adding to that collection is how the join row gets written below.
        var person = await personReader.Query()
            .Include(x => x.ScopeOwnerships)
            .FirstOrDefaultAsync(x =>
                x.PublicId == command.PersonId && !x.IsDeleted && x.RoleId == (long)Roles.ScopeAdmin);

        if (person is null)
        {
            return output.WithError(PersonMessages.PersonNotValidScopeAdmin);
        }

        // AF-21d: already an owner, so there is nothing to write — UC-21 step 4 calls the insert a
        // no-op in that case. Writing anyway would violate the (ScopeId, PersonId) composite key.
        if (person.ScopeOwnerships.Any(ownership => ownership.ScopeId == scope.Id))
        {
            return output
                .WithData(new AddScopeOwnerCommandOutput
                {
                    ScopeId = scope.PublicId, PersonId = person.PublicId, AlreadyOwner = true
                })
                .WithMessage(PersonMessages.AlreadyScopeOwner);
        }

        // UC-21 step 4: insert the SCOPE_OWNER row. ScopeOwner is a join entity with no repository of
        // its own, so the row is written through the person aggregate — the symmetric operation to
        // UC-08 clearing ScopeOwnerships to delete rows. FR-PE-11 needs no guard here: the person is
        // already a ScopeAdmin and this only ever increases the scopes they own.
        person.ScopeOwnerships.Add(new ScopeOwner { ScopeId = scope.Id, PersonId = person.Id });

        var update = await personWriter.UpdateAsync(person);

        if (!update.Success)
        {
            return output.WithErrors(update.Errors);
        }

        // UC-21 step 5.
        return output
            .WithData(new AddScopeOwnerCommandOutput
            {
                ScopeId = scope.PublicId, PersonId = person.PublicId, AlreadyOwner = false
            })
            .WithMessage(PersonMessages.ScopeOwnerAddedSuccessfully);
    }
}
