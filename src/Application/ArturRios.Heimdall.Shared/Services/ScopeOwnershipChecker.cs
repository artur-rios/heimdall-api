using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Heimdall.Shared.Services;

/// <summary>
///     Default <see cref="IScopeOwnershipChecker" />: a System Admin bypasses ownership; any other
///     actor is authorized only when a <c>SCOPE_OWNER</c> row links their person id to the scope.
/// </summary>
public class ScopeOwnershipChecker(IAsyncReadOnlyRepository<Person> personReader) : IScopeOwnershipChecker
{
    public async Task<bool> ActorMayManageScopeAsync(int actingRole, Guid actingPersonId, long scopeId)
    {
        // A System Admin bypasses the ownership check entirely (no query needed).
        if (actingRole == (int)Roles.SystemAdmin)
        {
            return true;
        }

        // Otherwise the actor must own the scope. A logically deleted person owns nothing: they can
        // no longer authenticate (UC-11 AF-11c), so a token issued before their deletion must not
        // keep working until it expires.
        return await personReader.Query().AnyAsync(person =>
            person.PublicId == actingPersonId && !person.IsDeleted &&
            person.ScopeOwnerships.Any(ownership => ownership.ScopeId == scopeId));
    }
}
