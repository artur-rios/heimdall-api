using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Command.Services;
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
/// <remarks>
///     <b>Under the scope's lock</b> (#202). The guard and the removal run in one transaction that holds
///     the scope's row (<see cref="IInvariantLocks" />), and so does every other writer that can take an
///     owner out of it. Two requests removing the scope's last two owners therefore run one after the
///     other: the second reads the first's removal and is refused, where both used to see the other
///     owner still there and both remove.
/// </remarks>
public class RemoveScopeOwnerCommandHandler(
    IAsyncReadOnlyRepository<Scope, long> scopeReader,
    IAsyncReadOnlyRepository<Person, long> personReader,
    IScopeOwnershipChecker scopeOwnership,
    IAtomicWrites atomicWrites,
    IWriteTransactions transactions,
    IInvariantLocks locks)
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

        // UC-22 steps 2-4 under the scope's lock (#202): leaving this block before the commit rolls back.
        await using var transaction = await transactions.BeginAsync();
        await locks.LockAsync([scope.Id]);

        // Read again under the lock: a deletion of the scope that committed since the read above answers
        // AF-22a as it would have a moment earlier.
        if (!await scopeReader.Query().AnyAsync(x => x.Id == scope.Id && !x.IsDeleted))
        {
            return output.WithError(PersonMessages.ScopeNotFound);
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

        // AF-22b (UC-22 step 3, NFR-12): somebody else must be left owning the scope. Only an active
        // co-owner counts (LastScopeOwnerGuard): one logically deleted, restricted or awaiting erasure
        // cannot administer the scope (#211), so without that exclusion an ownerless scope could be
        // created by removing the only active owner while an inactive one remained on the row.
        if (await LastScopeOwnerGuard.WouldStripLastOwnerAsync(person.Id, [scope.Id], personReader))
        {
            return output.WithError(PersonMessages.ScopeWouldLoseLastOwner);
        }

        // UC-22 step 4: delete the SCOPE_OWNER row, by a targeted write of that row alone (#237) rather
        // than a save of the person aggregate, which would write the person's profile back as read —
        // and this endpoint reads deleted persons too, whose name anonymisation may have replaced
        // meanwhile. The write re-checks AF-22b itself; under the lock no owner-removing writer can have
        // run since the check above, but a co-owner restricting their own processing (UC-44) takes no
        // lock, and the write sees one that committed. FR-PE-11 is deliberately not guarded here: SRD §8
        // names UC-22 as an operation that may leave a ScopeAdmin owning no scope.
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

        await transaction.CommitAsync();

        // UC-22 step 5.
        return output
            .WithData(new RemoveScopeOwnerCommandOutput { ScopeId = scope.PublicId, PersonId = person.PublicId })
            .WithMessage(PersonMessages.ScopeOwnerRemovedSuccessfully);
    }
}
