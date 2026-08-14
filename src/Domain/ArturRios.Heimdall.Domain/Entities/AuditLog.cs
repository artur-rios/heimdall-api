using System.ComponentModel.DataAnnotations;
using ArturRios.Data.Relational.Core.Entities;

namespace ArturRios.Heimdall.Domain.Entities;

/// <summary>
///     One audit trail entry per attempted write operation (NFR-09), whether it succeeded or not.
///     Append-only: never updated or logically deleted after creation. <see cref="ActorPersonId" />
///     is a bare <c>PublicId</c>, not a foreign key, so an entry survives a hard-deleted person.
/// </summary>
public class AuditLog : Entity
{
    /// <summary>External identifier of this entry.</summary>
    public Guid PublicId { get; set; } = Guid.NewGuid();

    /// <summary>The acting person's <c>PublicId</c>; <c>null</c> for an anonymous write.</summary>
    public Guid? ActorPersonId { get; set; }

    /// <summary>The acting person's role value (see <c>Roles</c>); <c>null</c> for an anonymous write.</summary>
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
