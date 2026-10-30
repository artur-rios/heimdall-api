using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Heimdall.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Heimdall.Command.Services;

/// <summary>
///     NFR-12: whether taking a person out of the scopes they own — removing one ownership (UC-22),
///     deleting them (UC-09, UC-10), or suspending them for erasure (UC-42 and the anonymisation pass's
///     retry) — would leave one of those scopes with no active owner.
/// </summary>
/// <remarks>
///     <para>
///         <b>One guard for every writer.</b> UC-09, UC-10 and UC-22 used to carry their own copies; the
///         definition of who keeps a scope owned now lives in one place, <see cref="Person.Active" />
///         (#211).
///     </para>
///     <para>
///         <b>Who counts.</b> Only an active co-owner: not logically deleted, no erasure requested, and
///         processing not restricted. A suspended owner cannot authenticate, a restricted one is refused
///         on every action but the data-subject ones and hidden from the owner list, and one whose
///         erasure is requested is on the way out — none of them can administer the scope, so none
///         keeps it owned.
///     </para>
///     <para>
///         <b>Asked under the scope locks (#202).</b> On its own the answer is stale the moment it is read:
///         two requests taking out the two owners of one scope each read the other still there, both
///         write, and nobody is left. Every caller therefore asks inside its transaction, once
///         <c>IInvariantLocks</c> holds the scopes in question — <see cref="LockedPerson.LockAsync" />
///         for a person's — so a competing writer of the same scope waits until the answer has been acted
///         on, and then reads what was written.
///     </para>
/// </remarks>
public static class LastScopeOwnerGuard
{
    /// <summary>
    ///     Whether any of <paramref name="scopeIds" /> is owned by nobody active but the person
    ///     <paramref name="personId" />.
    /// </summary>
    public static async Task<bool> WouldStripLastOwnerAsync(
        long personId, IReadOnlyCollection<long> scopeIds, IAsyncReadOnlyRepository<Person, long> personReader)
    {
        // Whatever the role: a SystemAdmin may own a scope too (FR-SC-08).
        if (scopeIds.Count == 0)
        {
            return false;
        }

        var coOwnedScopeIds = await personReader.Query()
            .Where(Person.Active)
            .Where(other => other.Id != personId)
            .SelectMany(other => other.ScopeOwnerships.Select(ownership => ownership.ScopeId))
            .Where(scopeId => scopeIds.Contains(scopeId))
            .Distinct()
            .ToListAsync();

        return scopeIds.Any(scopeId => !coOwnedScopeIds.Contains(scopeId));
    }
}
