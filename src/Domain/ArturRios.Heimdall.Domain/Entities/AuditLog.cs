using System.ComponentModel.DataAnnotations;
using ArturRios.Data.Relational.Core.Entities;

namespace ArturRios.Heimdall.Domain.Entities;

/// <summary>
///     One audit trail entry per attempted write operation (NFR-09), whether it succeeded or not.
///     <see cref="ActorPersonId" /> is a bare <c>PublicId</c>, not a foreign key, so an entry
///     survives a hard-deleted person.
/// </summary>
/// <remarks>
///     <para>
///         <b>Append-only, with exactly one exception.</b> Database triggers refuse every
///         <c>DELETE</c> and <c>TRUNCATE</c>, and refuse any <c>UPDATE</c> other than clearing
///         <see cref="ActorPersonId" /> and <see cref="ActorRole" /> — which may be set to
///         <c>null</c>, never to somebody else, and only once due (NFR-21). The resulting guarantee
///         is precise: an action can never be denied, and who took it can never be reassigned — only
///         forgotten, on schedule or on erasure.
///     </para>
///     <para>
///         That exception exists because the trail holds personal data. An indefinitely retained,
///         unalterable row naming a person who has exercised their right to erasure is personal data
///         processed with no remaining basis. Clearing the attribution leaves what happened, when and
///         how often intact while the entry stops relating to an identifiable person at all.
///     </para>
/// </remarks>
public class AuditLog : Entity
{
    /// <summary>External identifier of this entry.</summary>
    public Guid PublicId { get; set; } = Guid.NewGuid();

    /// <summary>
    ///     The acting person's <c>PublicId</c>; <c>null</c> for an anonymous write, and <c>null</c>
    ///     again once the attribution has been cleared (NFR-21).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This is the one column on this entity that may change after creation, and it may only
    ///         ever change to <c>null</c>. Everything else is immutable, and the row can never be
    ///         deleted — see the remarks on the class.
    ///     </para>
    ///     <para>
    ///         Two grounds make a clearing due: the entry is past the configured attribution period,
    ///         or the identity it names has been anonymised. Both are enforced by the database, not
    ///         here.
    ///     </para>
    /// </remarks>
    public Guid? ActorPersonId { get; set; }

    /// <summary>
    ///     The acting person's role value (see <c>Roles</c>); <c>null</c> for an anonymous write, and
    ///     cleared alongside <see cref="ActorPersonId" />.
    /// </summary>
    public int? ActorRole { get; set; }

    /// <summary>The command's CLR type name, e.g. <c>"CreateApplicationCommand"</c>.</summary>
    [MaxLength(200)]
    public string Action { get; set; } = string.Empty;

    /// <summary>Best-effort public identifier of the entity the write affected; <c>null</c> if none could be resolved.</summary>
    public Guid? TargetId { get; set; }

    /// <summary>
    ///     Whether the operation succeeded. The trail records refusals as well as writes, because a
    ///     refused attempt is usually the more interesting one: a caller repeatedly denied a scope
    ///     they do not own, or repeatedly failing a password, leaves no other trace.
    /// </summary>
    public bool Succeeded { get; set; }

    /// <summary>
    ///     The first error the operation reported, when it failed; <c>null</c> on success.
    /// </summary>
    /// <remarks>
    ///     One of the application's own canonical messages, or one of the persistence layer's
    ///     classified failures — never a caller-supplied value and never provider text, so the trail
    ///     cannot become a place where a submitted password or an address ends up recorded verbatim.
    /// </remarks>
    [MaxLength(500)]
    public string? FailureReason { get; set; }

    /// <summary>When the entry was written.</summary>
    public DateTime CreatedAt { get; set; }
}
