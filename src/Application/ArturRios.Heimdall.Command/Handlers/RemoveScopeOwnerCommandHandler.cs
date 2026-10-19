using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Persistence;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Heimdall.Shared.Services;
using ArturRios.Mediator.Command.Interfaces;
using ArturRios.Output;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Heimdall.Command.Handlers;

/// <summary>
///     Handles <see cref="RemoveScopeOwnerCommand" /> (UC-22, FR-SC-08/FR-SC-10): verifies the target
///     scope exists and is active (AF-22a), enforces scope ownership for a Scope Admin actor (AF-22c),
///     verifies the named person actually holds a <c>SCOPE_OWNER</c> row for that scope (AF-22a),
///     refuses to strip the scope of its last live owner (AF-22b, NFR-12), then removes the join row.
///     A System Admin actor bypasses the ownership check. All failures are returned as errors on the
///     <see cref="DataOutput{T}" /> rather than thrown.
/// </summary>
public class RemoveScopeOwnerCommandHandler(
    IAsyncReadOnlyRepository<Scope, long> scopeReader,
    IAsyncReadOnlyRepository<Person, long> personReader,
    IScopeOwnershipChecker scopeOwnership,
    IAtomicWrites atomicWrites)
    : ICommandHandlerAsync<RemoveScopeOwnerCommand, RemoveScopeOwnerCommandOutput>
{
    public async Task<DataOutput<RemoveScopeOwnerCommandOutput?>> HandleAsync(RemoveScopeOwnerCommand command, CancellationToken cancellationToken = default)
    {
        var output = DataOutput<RemoveScopeOwnerCommandOutput?>.New;

        // AF-22a (UC-22 step 2): the target scope must exist. The !IsDeleted filter answers alike for a
        // logically deleted one — rewriting the ownership of a scope withdrawn from service is not
        // something UC-22 promises, and every scope-scoped handler treats it as absent.
        var scope = await scopeReader.Query()
            .FirstOrDefaultAsync(x => x.PublicId == command.ScopeId && !x.IsDeleted);

        if (scope is null)
        {
            return output.WithError(PersonMessages.ScopeNotFound);
        }

        // AF-22c: a Scope Admin actor may only act on a scope they own; a System Admin bypasses.
        // Checked before the person is looked up so a caller who fails here learns nothing about which
        // person ids exist or who owns the scope.
        if (!await scopeOwnership.ActorMayManageScopeAsync(command.ActingRole, command.ActingPersonId, scope.Id))
        {
            return output.WithError(PersonMessages.NotScopeOwner);
        }

        // AF-22a (UC-22 step 2): the person must hold an ownership row for this scope. Neither
        // !IsDeleted nor a role filter applies — what matters is only whether the SCOPE_OWNER row
        // exists. A logically deleted ScopeAdmin keeps their ownership rows (UC-09 cascades nothing),
        // and clearing such a stale row is exactly what this endpoint is for.
        var person = await personReader.Query()
            .Include(x => x.ScopeOwnerships)
            .FirstOrDefaultAsync(x => x.PublicId == command.PersonId);

        var ownership = person?.ScopeOwnerships.FirstOrDefault(x => x.ScopeId == scope.Id);

        if (person is null || ownership is null)
        {
            return output.WithError(PersonMessages.PersonNotScopeOwner);
        }

        // AF-22b (UC-22 step 3, NFR-12): somebody else must be left owning the scope. Logically deleted
        // co-owners do not count — they can no longer authenticate (FR-AU-07), so they do not keep a
        // scope owned; without that exclusion an ownerless scope could be created by removing the only
        // live owner while a deleted one remained on the row.
        var hasLiveCoOwner = await personReader.Query().AnyAsync(other =>
            other.Id != person.Id && !other.IsDeleted &&
            other.ScopeOwnerships.Any(x => x.ScopeId == scope.Id));

        if (!hasLiveCoOwner)
        {
            return output.WithError(PersonMessages.ScopeWouldLoseLastOwner);
        }

        // UC-22 step 4: delete the SCOPE_OWNER row, by a targeted write of that row alone (#237) rather
        // than a save of the person aggregate, which would write the person's profile back as read —
        // and this endpoint reads deleted persons too, whose name anonymisation may have replaced
        // meanwhile. The write re-checks AF-22b itself, so a co-owner deleted since the check above is
        // seen. FR-PE-11 is deliberately not guarded here: SRD §8 names UC-22 as an operation that may
        // leave a ScopeAdmin owning no scope.
        if (!await atomicWrites.TryRemoveScopeOwnerAsync(person, scope.Id))
        {
            // Refused: either another request removed the row meanwhile, which is AF-22a's outcome, or
            // the last live co-owner went, which is AF-22b's. The row tells which.
            var ownsStill = await personReader.Query()
                .AnyAsync(x => x.Id == person.Id && x.ScopeOwnerships.Any(owned => owned.ScopeId == scope.Id));

            return output.WithError(ownsStill
                ? PersonMessages.ScopeWouldLoseLastOwner
                : PersonMessages.PersonNotScopeOwner);
        }

        // UC-22 step 5.
        return output
            .WithData(new RemoveScopeOwnerCommandOutput { ScopeId = scope.PublicId, PersonId = person.PublicId })
            .WithMessage(PersonMessages.ScopeOwnerRemovedSuccessfully);
    }
}
