using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Heimdall.Command.Services;

/// <summary>
///     NFR-12: whether suspending a person would leave a scope they own with no owner at all.
/// </summary>
/// <remarks>
///     <para>
///         The erasure request (UC-42) and the anonymisation pass (NFR-20) both need this answer —
///         one to decide whether the request can take effect, the other to decide whether a blocked
///         request can now proceed — so it is written once here rather than twice more.
///     </para>
///     <para>
///         Three handlers predating this carry their own copies of the same guard
///         (<c>DeletePersonCommandHandler</c>, <c>HardDeletePersonCommandHandler</c>,
///         <c>UpdatePersonCommandHandler</c>). Consolidating those is worth doing and is deliberately
///         not done here: they are covered by their own tests, and folding four call sites into one
///         belongs in a change about that rather than tacked onto this one.
///     </para>
/// </remarks>
public static class LastScopeOwnerGuard
{
    /// <summary>
    ///     Whether any scope <paramref name="person" /> owns is owned by nobody else who is still
    ///     live. Requires <c>ScopeOwnerships</c> to have been loaded.
    /// </summary>
    public static async Task<bool> WouldStripLastOwnerAsync(
        Person person, IAsyncReadOnlyRepository<Person> personReader)
    {
        if (person.RoleId != (long)Roles.ScopeAdmin || person.ScopeOwnerships.Count == 0)
        {
            return false;
        }

        // Persons already logically deleted are excluded: a suspended owner cannot authenticate, so
        // they no longer keep a scope owned.
        var coOwnedScopeIds = await personReader.Query()
            .Where(other => other.Id != person.Id && !other.IsDeleted)
            .SelectMany(other => other.ScopeOwnerships.Select(ownership => ownership.ScopeId))
            .Distinct()
            .ToListAsync();

        return person.ScopeOwnerships.Any(ownership => !coOwnedScopeIds.Contains(ownership.ScopeId));
    }
}
