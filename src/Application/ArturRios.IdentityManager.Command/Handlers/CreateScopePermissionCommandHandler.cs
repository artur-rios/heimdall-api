using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.IdentityManager.Command.Input;
using ArturRios.IdentityManager.Command.Output;
using ArturRios.IdentityManager.Domain.Entities;
using ArturRios.IdentityManager.Shared.Messages;
using ArturRios.IdentityManager.Shared.Services;
using ArturRios.Mediator.Command.Interfaces;
using ArturRios.Output;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Scope = ArturRios.IdentityManager.Domain.Entities.Scope;

namespace ArturRios.IdentityManager.Command.Handlers;

/// <summary>
///     Handles <see cref="CreateScopePermissionCommand" /> (UC-31, FR-SP-01/02): validates input
///     (AF-31d), verifies the target scope exists and is active (AF-31a), enforces the acting role's
///     rule — a Scope Admin must own the scope (AF-31e), a System Admin bypasses it — then creates the
///     scope-permission record. A scope permission is a scope-child resource with no separate owner,
///     so the scope-ownership check is the only authorization.
/// </summary>
public class CreateScopePermissionCommandHandler(
    IValidator<CreateScopePermissionCommand> validator,
    IAsyncReadOnlyRepository<Scope> scopeReader,
    IAsyncRepository<ScopePermission> permissionWriter,
    IScopeOwnershipChecker scopeOwnership)
    : ICommandHandlerAsync<CreateScopePermissionCommand, CreateScopePermissionCommandOutput>
{
    public async Task<DataOutput<CreateScopePermissionCommandOutput?>> HandleAsync(
        CreateScopePermissionCommand command)
    {
        var output = DataOutput<CreateScopePermissionCommandOutput?>.New;

        // AF-31d: validate input shape.
        var validation = await validator.ValidateAsync(command);

        if (!validation.IsValid)
        {
            return output.WithErrors(validation.Errors.Select(failure => failure.ErrorMessage));
        }

        // AF-31a: the target scope must exist and not be logically deleted.
        var scope = await scopeReader.Query()
            .FirstOrDefaultAsync(x => x.PublicId == command.ScopeId && !x.IsDeleted);

        if (scope is null)
        {
            return output.WithError(ScopePermissionMessages.ScopeNotFound);
        }

        // AF-31e: a Scope Admin actor may only act on a scope they own; a System Admin bypasses the
        // check inside the checker.
        if (!await scopeOwnership.ActorMayManageScopeAsync(
                command.ActingRole, command.ActingPersonId, scope.Id))
        {
            return output.WithError(ScopePermissionMessages.NotScopeOwner);
        }

        // FR-SP-01/02: create the permission in the scope, active.
        var permission = new ScopePermission
        {
            Name = command.Name,
            Description = command.Description,
            IncludeAsJwtClaim = command.IncludeAsJwtClaim,
            IsDeleted = false,
            ScopeId = scope.Id
        };

        var creation = await permissionWriter.CreateAsync(permission);

        if (!creation.Success)
        {
            return output.WithErrors(creation.Errors);
        }

        return output
            .WithData(new CreateScopePermissionCommandOutput
            {
                Id = permission.PublicId,
                Name = permission.Name,
                Description = permission.Description,
                IncludeAsJwtClaim = permission.IncludeAsJwtClaim,
                ScopeId = scope.PublicId,
                CreatedAt = permission.CreatedAt
            })
            .WithMessage(ScopePermissionMessages.ScopePermissionCreatedSuccessfully);
    }
}