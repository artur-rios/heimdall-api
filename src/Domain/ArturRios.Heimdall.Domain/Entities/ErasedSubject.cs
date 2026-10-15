using ArturRios.Data.Relational.Core.Entities;

namespace ArturRios.Heimdall.Domain.Entities;

/// <summary>
///     A tombstone: the public id of a person or Google User that was hard-deleted, kept only until
///     the audit trail no longer names it (NFR-21, #195).
/// </summary>
/// <remarks>
///     <para>
///         <b>Why it exists.</b> An audit entry's <see cref="AuditLog.TargetId" /> is a bare
///         <c>PublicId</c>, not a foreign key, so it outlives the row it named. The database clears a
///         target only once it can tell the target was a person, and after a hard delete nothing is
///         left to tell it so: the entries about that person, and the hard delete's own entry, kept
///         an id that a backup, an export or a log line could still join back to them. This row is
///         that evidence, written before the delete, so the retention pass may clear those targets
///         and the trigger installed by <c>AllowClearingErasedSubjectTarget</c> may accept it.
///     </para>
///     <para>
///         <b>What it holds.</b> The id, which table it came from, and when — nothing else about the
///         person. It is the processing record of a deletion, and it is short-lived: the pass removes
///         it once it is a day old and no audit entry names it any more.
///     </para>
///     <para>
///         <b>The key is the public id.</b> <c>Id</c> holds the removed identity's <c>PublicId</c>,
///         stored as <c>public_id</c>; there is no surrogate key. The table is looked up by the id
///         the trail carries and holds each id at most once, so a hard delete that is retried writes
///         no second row.
///     </para>
/// </remarks>
public class ErasedSubject : Entity<Guid>
{
    /// <summary>
    ///     Which table the id was taken from (see <c>ErasedSubjectKinds</c>).
    /// </summary>
    public int Kind { get; set; }

    /// <summary>
    ///     When the tombstone was written, just before the delete. The pass keeps it for a day at
    ///     least, whether or not anything names it yet.
    /// </summary>
    public DateTime CreatedAt { get; set; }
}
