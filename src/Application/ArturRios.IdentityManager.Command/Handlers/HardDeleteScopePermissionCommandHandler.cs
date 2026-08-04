using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.IdentityManager.Command.Input;
using ArturRios.IdentityManager.Command.Output;
using ArturRios.IdentityManager.Domain.Entities;
using ArturRios.IdentityManager.Shared.Messages;
using ArturRios.Mediator.Command.Interfaces;
using ArturRios.Output;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.IdentityManager.Command.Handlers;

/// <summary>
///     Handles <see cref="HardDeleteScopePermissionCommand" /> (UC-35, FR-SP-08): locates the
///     permission inside the addressed scope in any deletion state (AF-35a) and permanently removes
///     the record. Nothing cascades — a scope permission is a leaf in the data model, and the scope
///     its foreign key points at is left intact. Authorization is entirely the endpoint's: UC-35's
///     only actor is the System Admin, so no rule is left for the handler to apply. All failures are
///     returned as errors on the <see cref="DataOutput{T}" /> rather than thrown.
/// </summary>
public class HardDeleteScopePermissionCommandHandler(
    IAsyncReadOnlyRepository<ScopePermission> permissionReader,
    IAsyncRepository<ScopePermission> permissionWriter)
    : ICommandHandlerAsync<HardDeleteScopePermissionCommand, HardDeleteScopePermissionCommandOutput>
{
    public async Task<DataOutput<HardDeleteScopePermissionCommandOutput?>> HandleAsync(
        HardDeleteScopePermissionCommand command)
    {
        var output = DataOutput<HardDeleteScopePermissionCommandOutput?>.New;

        // AF-35a: the permission must exist inside the addressed scope. The lookup omits an
        // !IsDeleted filter — a permission logically deleted by UC-34 is exactly what a cleanup pass
        // starts from and must still be purgeable. The
        // route's scopeId qualifies it: an unknown permission, an unknown scope, and a permission
        // living in another scope are all one 404, because the addressed resource genuinely does not
        // exist in any of the three cases. A repeated call lands here too — the row is already gone,
        // and UC-35 has no idempotent path.
        var permission = await permissionReader.Query()
            .FirstOrDefaultAsync(x => x.PublicId == command.Id && x.Scope.PublicId == command.ScopeId);

        if (permission is null)
        {
            return output.WithError(ScopePermissionMessages.ScopePermissionNotFound);
        }

        // UC-35 step 2: remove the record for good. No dependent is deleted first — no entity carries
        // a foreign key to a scope permission, so no foreign key can be violated and there is no
        // total to report.
        var delete = await permissionWriter.DeleteAsync(permission);

        if (!delete.Success)
        {
            return output.WithErrors(delete.Errors);
        }

        // UC-35 step 3.
        return output
            .WithData(new HardDeleteScopePermissionCommandOutput { Id = permission.PublicId })
            .WithMessage(ScopePermissionMessages.ScopePermissionHardDeletedSuccessfully);
    }
}