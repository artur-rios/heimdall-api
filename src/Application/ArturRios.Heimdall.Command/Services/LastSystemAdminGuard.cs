using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Heimdall.Command.Services;

/// <summary>
///     NFR-32: whether taking a System Admin out — deleting them (UC-09, UC-10) or suspending them for erasure
///     (UC-42 and the anonymisation pass's retry) — would leave the system without an active System Admin.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why.</b> Only a System Admin creates scopes, changes roles and reaches the retention tooling, and
///         the seeder re-creates the master user only on the next start, and only when its variables are set.
///         An administrator deleted or erased by mistake, or two deleting each other at once, would otherwise
///         lock everyone out until then (#203).
///     </para>
///     <para>
///         <b>Who counts.</b> An active System Admin (<see cref="Person.Active" />): not logically deleted, no
///         erasure requested, processing not restricted — the same definition NFR-12 uses for owners. A
///         restriction is the subject's right and is never refused (UC-44), so the rule is that no
///         administrator's <em>removal</em> may leave none, not that one always exists.
///     </para>
///     <para>
///         <b>Asked under the System Admin lock.</b> Every caller asks inside its transaction, once
///         <c>IInvariantLocks</c> holds the System Admin set, and decides whether the guard applies from the
///         person as read under it (<see cref="LockedPerson.IsLiveSystemAdmin" />), so two administrators
///         removing each other at once run one after the other, and the second is refused.
///     </para>
/// </remarks>
public static class LastSystemAdminGuard
{
    /// <summary>
    ///     Whether no System Admin but the person <paramref name="personId" /> is active, so taking them out
    ///     would leave the system without one.
    /// </summary>
    public static async Task<bool> WouldLeaveNoneAsync(
        long personId, IAsyncReadOnlyRepository<Person, long> personReader) =>
        !await personReader.Query()
            .Where(Person.Active)
            .AnyAsync(other => other.Id != personId && other.RoleId == (long)Roles.SystemAdmin);
}
