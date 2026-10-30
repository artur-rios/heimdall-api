using ArturRios.Heimdall.Domain.Entities;

namespace ArturRios.Heimdall.Domain.Persistence;

/// <summary>
///     Writes the tombstones of hard-deleted persons and Google Users (NFR-21, #195): see
///     <see cref="ErasedSubject" />.
/// </summary>
/// <remarks>
///     <para>
///         <b>Before the delete, not after.</b> Each hard delete records its ids first. UC-10 and UC-05
///         do so inside the transaction of their deletes (#212), so a failed delete keeps no tombstone.
///         Where one is kept for a live row — UC-29's, written before its single delete — it is
///         harmless: the retention pass and the trigger act only on an id that no longer resolves to any
///         <c>person</c> or <c>google_user</c> row. The other order could leave a removed person with no
///         tombstone, named in the trail for good.
///     </para>
///     <para>
///         <b>Idempotent.</b> A retried hard delete records the same ids again, and that is not an
///         error: an id already recorded keeps its one row, its <c>CreatedAt</c> moved to the new
///         attempt, so the day of grace counts from the delete that actually happens. Reads and the
///         pass's cleanup go through the ordinary repositories; only this write needs its own seam,
///         because a plain insert fails on the primary key the second time.
///     </para>
/// </remarks>
public interface IErasedSubjects
{
    /// <summary>
    ///     Records every subject in <paramref name="subjects" />, as one statement, inserting the ids
    ///     not yet recorded and refreshing <c>CreatedAt</c> on those that are.
    /// </summary>
    Task RecordAsync(IReadOnlyCollection<ErasedSubject> subjects);
}
