using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Heimdall.Shared.Services;
using ArturRios.Mediator.Command.Interfaces;
using ArturRios.Output;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Heimdall.Command.Handlers;

/// <summary>
///     Handles <see cref="UpdateScopePermissionCommand" /> (UC-33, FR-SP-06): validates input,
///     loads the permission inside the addressed scope (AF-33a), enforces the acting role's rule — a
///     System Admin may update any permission, anyone else only one whose scope they own (AF-33e) —
///     then applies the changes and stamps <c>UpdatedAt</c>. All failures are returned as errors on
///     the output rather than thrown.
/// </summary>
public class UpdateScopePermissionCommandHandler(
    IValidator<UpdateScopePermissionCommand> validator,
    IAsyncReadOnlyRepository<ScopePermission> permissionReader,
    IAsyncRepository<ScopePermission> permissionWriter,
    IScopeOwnershipChecker scopeOwnership)
    : ICommandHandlerAsync<UpdateScopePermissionCommand, UpdateScopePermissionCommandOutput>
{
    public async Task<DataOutput<UpdateScopePermissionCommandOutput?>> HandleAsync(
        UpdateScopePermissionCommand command)
    {
        var output = DataOutput<UpdateScopePermissionCommandOutput?>.New;

        // UC-33 step 2: validate input shape. UC-33 defines no alternative flow for this, so the
        // validator's UC-31 messages carry their existing 400.
        var validation = await validator.ValidateAsync(command);

        if (!validation.IsValid)
        {
            return output.WithErrors(validation.Errors.Select(failure => failure.ErrorMessage));
        }

        // AF-33a: the permission must exist inside the addressed scope and not be logically deleted.
        // The route's scopeId qualifies the lookup, as UC-32's by-id read does: a permission that
        // lives in another scope is not the resource this path addresses. Checked before
        // authorization so AF-33a and AF-33e both stay observable.
        var permission = await permissionReader.Query()
            .Include(x => x.Scope)
            .FirstOrDefaultAsync(x =>
                x.PublicId == command.Id && x.Scope.PublicId == command.ScopeId && !x.IsDeleted);

        if (permission is null)
        {
            return output.WithError(ScopePermissionMessages.ScopePermissionNotFound);
        }

        // AF-33e: a System Admin may update any scope's permission; anyone else must own the scope.
        // A scope permission carries no separate owner, so the scope-ownership check is the only
        // authorization. Runs before any mutation so AF-33e stays observable on otherwise-no-op
        // updates.
        if (!await scopeOwnership.ActorMayManageScopeAsync(
                command.ActingRole, command.ActingPersonId, permission.ScopeId))
        {
            return output.WithError(ScopePermissionMessages.NotScopeOwner);
        }

        // UC-33 step 5: apply the updates and stamp UpdatedAt (no DB trigger maintains it).
        permission.Name = command.Name;
        permission.Description = command.Description;
        permission.IncludeAsJwtClaim = command.IncludeAsJwtClaim;
        permission.UpdatedAt = DateTime.UtcNow;

        var update = await permissionWriter.UpdateAsync(permission);

        if (!update.Success)
        {
            return output.WithErrors(update.Errors);
        }

        // UC-33 step 6: return the updated permission.
        return output
            .WithData(new UpdateScopePermissionCommandOutput
            {
                Id = permission.PublicId,
                Name = permission.Name,
                Description = permission.Description,
                IncludeAsJwtClaim = permission.IncludeAsJwtClaim,
                ScopeId = permission.Scope.PublicId,
                CreatedAt = permission.CreatedAt,
                UpdatedAt = permission.UpdatedAt
            })
            .WithMessage(ScopePermissionMessages.ScopePermissionUpdatedSuccessfully);
    }
}