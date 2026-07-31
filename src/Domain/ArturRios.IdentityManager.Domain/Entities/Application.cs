using System.ComponentModel.DataAnnotations;
using ArturRios.Data.Relational.Core.Entities;

namespace ArturRios.IdentityManager.Domain.Entities;

/// <summary>
///     A registered non-human identity representing another system. Belongs to exactly one
///     <see cref="Scope" /> and is owned by exactly one <see cref="Person" /> — an existing,
///     non-logically-deleted <c>ScopeAdmin</c> who owns that scope. A <c>User</c> may never own an
///     application (FR-AP-03).
/// </summary>
public class Application : Entity
{
    /// <summary>
    ///     External identifier, generated on creation and used everywhere the application is
    ///     addressed from outside the database (API paths, response bodies). The internal
    ///     <see cref="Entity.Id" /> is never exposed to callers.
    /// </summary>
    public Guid PublicId { get; set; } = Guid.NewGuid();

    /// <summary>Application display name. Required, max 200 characters.</summary>
    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    /// <summary>Logical deletion flag. Logically deleted applications are excluded from default queries.</summary>
    public bool IsDeleted { get; set; }

    /// <summary>Foreign key to the owning <see cref="Scope" /> (internal Id). Required.</summary>
    public long ScopeId { get; set; }

    /// <summary>Foreign key to the owning <see cref="Person" /> (internal Id). Required.</summary>
    public long OwnerId { get; set; }

    /// <summary>Creation timestamp.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>Last update timestamp.</summary>
    public DateTime UpdatedAt { get; set; }

    // Navigation properties

    /// <summary>The scope this application belongs to.</summary>
    public Scope Scope { get; set; } = null!;

    /// <summary>The person that owns this application.</summary>
    public Person Owner { get; set; } = null!;
}
