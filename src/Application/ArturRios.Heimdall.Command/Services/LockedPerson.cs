using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Domain.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Heimdall.Command.Services;

/// <summary>
///     A person about to be taken out — deleted (UC-09, UC-10) or suspended for erasure (UC-42 and the
///     anonymisation pass's retry) — as they stand once the invariant locks are held: their role, whether they
///     are deleted, and the scopes they own. What <see cref="LastScopeOwnerGuard" /> and
///     <see cref="LastSystemAdminGuard" /> decide on.
/// </summary>
/// <remarks>
///     <para>
///         <b>Read under the locks, not before them</b> (#202, #203). A copy read before the transaction can
///         be stale by the time the guards ask: a scope the person owned may have been hard-deleted, so its row
///         is gone from the lock and the guard would refuse for a scope that no longer exists; or the person may
///         have been promoted to System Admin, and a role read before the lock would skip NFR-32's guard.
///     </para>
///     <para>
///         <b>The System Admin set is locked for every one of them.</b> Which persons are administrators is
///         only known under the lock, so the lock cannot be chosen by the role: a User or Scope Admin promoted
///         while being deleted would otherwise be deleted as an administrator with no lock held. These writers
///         are administrative and rare, so serialising them on one key costs nothing that matters.
///     </para>
///     <para>
///         <b>An ownership gained after the locks</b> — a scope created naming the person (UC-03), an owner
///         added (UC-21, UC-23) — is neither locked nor checked: the writers that add owners take no lock, since
///         adding one can never leave a scope ownerless on its own. The System Requirements record that
///         residual.
///     </para>
/// </remarks>
/// <param name="RoleId">The person's role under the locks.</param>
/// <param name="IsDeleted">Whether the person is logically deleted under the locks.</param>
/// <param name="OwnedScopeIds">The scopes the person owns under the locks.</param>
public sealed record LockedPerson(long RoleId, bool IsDeleted, IReadOnlyCollection<long> OwnedScopeIds)
{
    /// <summary>Whether the person is a System Admin NFR-32 protects: one not yet deleted.</summary>
    public bool IsLiveSystemAdmin => RoleId == (long)Roles.SystemAdmin && !IsDeleted;

    /// <summary>
    ///     Locks the scopes the person <paramref name="personId" /> owns, then the System Admin set — in the one
    ///     <see cref="IInvariantLocks.LockAsync" /> call the lock order asks for, in the caller's transaction —
    ///     and reads the person again under them. <c>null</c> when the person is gone.
    /// </summary>
    public static async Task<LockedPerson?> LockAsync(
        IInvariantLocks locks, IAsyncReadOnlyRepository<Person, long> personReader, long personId)
    {
        var scopeIds = await personReader.Query()
            .Where(person => person.Id == personId)
            .SelectMany(person => person.ScopeOwnerships.Select(ownership => ownership.ScopeId))
            .ToListAsync();

        await locks.LockAsync(scopeIds, systemAdmins: true);

        // A projection, so the values are the database's as they stand now rather than a tracked copy's.
        // The ownerships are those the person still holds: a scope deleted since the read above is gone
        // from them, as it is from the lock.
        var current = await personReader.Query()
            .Where(person => person.Id == personId)
            .Select(person => new
            {
                person.RoleId,
                person.IsDeleted,
                OwnedScopeIds = person.ScopeOwnerships.Select(ownership => ownership.ScopeId).ToList()
            })
            .FirstOrDefaultAsync();

        return current is null ? null : new LockedPerson(current.RoleId, current.IsDeleted, current.OwnedScopeIds);
    }
}
