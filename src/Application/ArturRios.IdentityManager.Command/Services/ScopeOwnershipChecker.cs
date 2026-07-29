using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.IdentityManager.Domain.Entities;
using ArturRios.IdentityManager.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.IdentityManager.Command.Services;

/// <summary>
///     Default <see cref="IScopeOwnershipChecker" />: a System Admin bypasses ownership; any other
///     actor is authorized only when a <c>SCOPE_OWNER</c> row links their person id to the scope.
/// </summary>
public class ScopeOwnershipChecker(IAsyncReadOnlyRepository<Person> personReader) : IScopeOwnershipChecker
{
    public async Task<bool> ActorMayManageScopeAsync(int actingRole, long actingPersonId, long scopeId)
    {
        // A System Admin bypasses the ownership check entirely (no query needed).
        if (actingRole == (int)Roles.SystemAdmin)
        {
            return true;
        }

        // Otherwise the actor must own the scope.
        return await personReader.Query().AnyAsync(person =>
            person.Id == actingPersonId &&
            person.ScopeOwnerships.Any(ownership => ownership.ScopeId == scopeId));
    }
}
