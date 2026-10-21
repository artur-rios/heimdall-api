using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Command.Services;
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
///     Handles <see cref="DeletePersonCommand" /> (UC-09): locates the person (AF-09a), refuses a
///     self-deletion (AF-09d), enforces the per-actor rule (AF-09c), serves an already-deleted person
///     as an idempotent no-op (AF-09b), and otherwise sets <c>IsDeleted = true</c> — provided no scope
///     would be left without an owner (AF-09e, NFR-12) and the system not without an active System Admin
///     (AF-09f, NFR-32). Nothing cascades: the person's join rows,
///     tokens, and owned applications are untouched. All failures are returned as errors on the
///     <see cref="DataOutput{T}" /> rather than thrown.
/// </summary>
/// <remarks>
///     <b>Under the locks of the person's scopes</b> (#202), and of the System Admin set when the person is
///     one (#203). The guards and the deletion run in one transaction holding the rows of the scopes the
///     person owns, and then the System Admin set (<see cref="IInvariantLocks" />), as every writer that
///     can take an owner or a System Admin out does. Deleting one of the last two while the other is
///     removed or deleted therefore runs one after the other, and the second is refused, where both used
///     to see the other still there.
/// </remarks>
public class DeletePersonCommandHandler(
    IAsyncReadOnlyRepository<Person, long> personReader,
    IScopeOwnershipChecker scopeOwnership,
    IAtomicWrites atomicWrites,
    IWriteTransactions transactions,
    IInvariantLocks locks)
    : ICommandHandlerAsync<DeletePersonCommand, DeletePersonCommandOutput>
{
    public async Task<DataOutput<DeletePersonCommandOutput?>> HandleAsync(DeletePersonCommand command, CancellationToken cancellationToken = default)
    {
        var output = DataOutput<DeletePersonCommandOutput?>.New;

        // AF-09a. The lookup deliberately omits an !IsDeleted filter (unlike UC-08) so an
        // already-deleted person is found and served idempotently by AF-09b below. ScopeMembership is
        // needed by the authorization rule; the ownerships the last-owner guard needs are read under its
        // locks.
        var person = await personReader.Query()
            .Include(x => x.ScopeMembership)
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

        // Steps 3-4 under the locks of the person's scopes (#202): leaving this block before the commit
        // rolls back.
        await using var transaction = await transactions.BeginAsync();

        var systemAdmin = LastSystemAdminGuard.Applies(person);
        var ownedScopeIds =
            await LastScopeOwnerGuard.LockOwnedScopesAsync(locks, personReader, person.Id, systemAdmin);

        // AF-09e (NFR-12): a soft-deleted owner can no longer authenticate, so deleting the last one
        // would leave the scope effectively ownerless. Asked under the locks, so it is still true when
        // the deletion below is written.
        if (await LastScopeOwnerGuard.WouldStripLastOwnerAsync(person.Id, ownedScopeIds, personReader))
        {
            return output.WithError(PersonMessages.ScopeWouldLoseLastOwner);
        }

        // AF-09f (NFR-32): nor may it leave the system without an active System Admin. AF-09d already
        // keeps an administrator from deleting themselves; this is the other one, deleted by someone
        // who — restricted, or deleting them at the same moment — would leave none.
        if (systemAdmin && await LastSystemAdminGuard.WouldLeaveNoneAsync(person.Id, personReader))
        {
            return output.WithError(PersonMessages.WouldRemoveLastSystemAdmin);
        }

        // UC-09 step 3: flip the flag and stamp UpdatedAt (no DB trigger maintains it).
        //
        // DeletedAt and DeletionKind are stamped alongside it for NFR-20: the window that ends in
        // anonymisation is measured from the deletion, and UpdatedAt cannot serve as that mark
        // because any later write moves it and would silently restart the window. The kind is
        // Administrative because this is an administrator's decision — no data subject asked — and
        // that carries the longer of the schedule's two deadlines; UC-42 writes SubjectRequested.
        //
        // A targeted write of those four columns alone (#237), and only while the person is live:
        // a deletion that landed since the read stands as it was, and this one answers as AF-09b
        // does, since the outcome the caller asked for is already the case.
        var deleted = await atomicWrites.TryMarkDeletedAsync(person, DeletionKinds.Administrative, DateTime.UtcNow);

        if (!deleted)
        {
            return output
                .WithData(new DeletePersonCommandOutput { Id = person.PublicId, AlreadyDeleted = true })
                .WithMessage(PersonMessages.PersonDeletedSuccessfully);
        }

        await transaction.CommitAsync();

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

}
