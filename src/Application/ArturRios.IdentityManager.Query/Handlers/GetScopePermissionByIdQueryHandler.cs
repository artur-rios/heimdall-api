using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.IdentityManager.Domain.Entities;
using ArturRios.IdentityManager.Query.Input;
using ArturRios.IdentityManager.Query.Output;
using ArturRios.IdentityManager.Shared.Messages;
using ArturRios.IdentityManager.Shared.Services;
using ArturRios.Mediator.Query.Interfaces;
using ArturRios.Output;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.IdentityManager.Query.Handlers;

/// <summary>
///     Handles <see cref="GetScopePermissionByIdQuery" /> (UC-32, FR-SP-04): retrieves a scope
///     permission by its <c>PublicId</c> within the addressed scope, excluding logically deleted
///     permissions unless explicitly requested (FR-SP-09), then applies the use case's visibility
///     rule — a System Admin sees any permission, anyone else must own the scope. A miss is AF-32a
///     (<c>ScopePermissionNotFound</c>); an actor who does not own the scope is AF-32e
///     (<c>NotScopeOwner</c>). Both are returned as errors rather than thrown. The permission's
///     internal scope key is needed for the ownership check, so the entity is loaded (not projected)
///     before authorization — mirroring the UC-34 delete handler.
/// </summary>
public class GetScopePermissionByIdQueryHandler(
    IAsyncReadOnlyRepository<ScopePermission> permissionReader,
    IScopeOwnershipChecker scopeOwnership)
    : IQueryHandlerAsync<GetScopePermissionByIdQuery, ScopePermissionOutput>
{
    public async Task<DataOutput<ScopePermissionOutput?>> HandleAsync(GetScopePermissionByIdQuery query)
    {
        var output = DataOutput<ScopePermissionOutput?>.New;

        // AF-32a: no such permission under this scope (or it is logically deleted and was not
        // explicitly requested). The route's scopeId qualifies the lookup, so a permission that
        // lives in another scope is not the resource this path addresses. Checked before
        // authorization, so AF-32a and AF-32e both stay observable — a GUID nobody holds cannot be
        // told apart from one the caller may not see.
        var permission = await permissionReader.Query()
            .Include(x => x.Scope)
            .FirstOrDefaultAsync(x => x.PublicId == query.Id &&
                                      x.Scope.PublicId == query.ScopeId &&
                                      (query.IncludeDeleted || !x.IsDeleted));

        if (permission is null)
        {
            return output.WithError(ScopePermissionMessages.ScopePermissionNotFound);
        }

        // AF-32e: a System Admin sees every permission; anyone else must own the scope. A scope
        // permission has no owner of its own, so owning the scope is the only authorization.
        if (!await scopeOwnership.ActorMayManageScopeAsync(
                query.ActingRole, query.ActingPersonId, permission.ScopeId))
        {
            return output.WithError(ScopePermissionMessages.NotScopeOwner);
        }

        return output
            .WithData(new ScopePermissionOutput
            {
                Id = permission.PublicId,
                Name = permission.Name,
                Description = permission.Description,
                IncludeAsJwtClaim = permission.IncludeAsJwtClaim,
                ScopeId = permission.Scope.PublicId,
                IsDeleted = permission.IsDeleted,
                CreatedAt = permission.CreatedAt,
                UpdatedAt = permission.UpdatedAt
            })
            .WithMessage(ScopePermissionMessages.ScopePermissionRetrievedSuccessfully);
    }
}
