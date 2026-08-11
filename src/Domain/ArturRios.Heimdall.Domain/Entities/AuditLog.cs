using ArturRios.Data.Relational.Core.Entities;

namespace ArturRios.Heimdall.Domain.Entities;

/// <summary>
///     One audit trail entry for a successful write operation (NFR-09). Append-only: never updated
///     or logically deleted after creation. <see cref="ActorPersonId" /> is a bare <c>PublicId</c>,
///     not a foreign key, so an entry survives a hard-deleted person.
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
    public string Action { get; set; } = string.Empty;

    /// <summary>Best-effort public identifier of the entity the write affected; <c>null</c> if none could be resolved.</summary>
    public Guid? TargetId { get; set; }

    /// <summary>When the entry was written.</summary>
    public DateTime CreatedAt { get; set; }
}
