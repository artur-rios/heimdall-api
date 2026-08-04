using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.IdentityManager.Command.Input;
using ArturRios.IdentityManager.Command.Output;
using ArturRios.IdentityManager.Domain.Entities;
using ArturRios.IdentityManager.Shared.Messages;
using ArturRios.IdentityManager.Shared.Services;
using ArturRios.Mediator.Command.Interfaces;
using ArturRios.Output;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.IdentityManager.Command.Handlers;

/// <summary>
///     Handles <see cref="DeleteScopePermissionCommand" /> (UC-34, FR-SP-07): locates the permission
///     inside the addressed scope (AF-34a), enforces the acting role's rule — a System Admin may
///     delete any permission, anyone else only one whose scope they own (AF-34e) — serves an
///     already-deleted permission as an idempotent no-op (AF-34b), and otherwise sets
///     <c>IsDeleted = true</c> and stamps <c>UpdatedAt</c>. Nothing cascades: a scope permission owns
///     no dependent row. All failures are returned as errors on the <see cref="DataOutput{T}" />
///     rather than thrown.
/// </summary>
public class DeleteScopePermissionCommandHandler(
    IAsyncReadOnlyRepository<ScopePermission> permissionReader,
    IAsyncRepository<ScopePermission> permissionWriter,
    IScopeOwnershipChecker scopeOwnership)
    : ICommandHandlerAsync<DeleteScopePermissionCommand, DeleteScopePermissionCommandOutput>
{
    public async Task<DataOutput<DeleteScopePermissionCommandOutput?>> HandleAsync(
        DeleteScopePermissionCommand command)
    {
        var output = DataOutput<DeleteScopePermissionCommandOutput?>.New;

        // AF-34a: the permission must exist inside the addressed scope. The lookup deliberately omits
        // the !IsDeleted filter UC-33 applies, so an already-deleted permission is found and served
        // idempotently by AF-34b below rather than reported as not found. The route's scopeId
        // qualifies it: a permission that lives in another scope is not the resource this path
        // addresses. Checked before authorization so AF-34a and AF-34e both stay observable.
        var permission = await permissionReader.Query()
            .Include(x => x.Scope)
            .FirstOrDefaultAsync(x => x.PublicId == command.Id && x.Scope.PublicId == command.ScopeId);

        if (permission is null)
        {
            return output.WithError(ScopePermissionMessages.ScopePermissionNotFound);
        }

        // AF-34e: a System Admin may delete any scope's permission; anyone else must own the scope.
        // Runs before AF-34b so an already-deleted permission cannot be used to probe for scopes the
        // caller may not act on.
        if (!await scopeOwnership.ActorMayManageScopeAsync(
                command.ActingRole, command.ActingPersonId, permission.ScopeId))
        {
            return output.WithError(ScopePermissionMessages.NotScopeOwner);
        }

        // AF-34b: already deleted — whether directly or by UC-04's cascade from its scope — so there
        // is nothing to write. UpdatedAt is left alone: the row already carries the requested state,
        // and re-stamping it would misreport when the deletion happened.
        if (permission.IsDeleted)
        {
            return output
                .WithData(new DeleteScopePermissionCommandOutput
                {
                    Id = permission.PublicId,
                    AlreadyDeleted = true
                })
                .WithMessage(ScopePermissionMessages.ScopePermissionDeletedSuccessfully);
        }

        // UC-34 step 3: flip the flag and stamp UpdatedAt (no DB trigger maintains it).
        permission.IsDeleted = true;
        permission.UpdatedAt = DateTime.UtcNow;

        var update = await permissionWriter.UpdateAsync(permission);

        if (!update.Success)
        {
            return output.WithErrors(update.Errors);
        }

        // UC-34 step 4.
        return output
            .WithData(new DeleteScopePermissionCommandOutput
            {
                Id = permission.PublicId,
                AlreadyDeleted = false
            })
            .WithMessage(ScopePermissionMessages.ScopePermissionDeletedSuccessfully);
    }
}