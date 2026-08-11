using System.ComponentModel.DataAnnotations;
using ArturRios.Data.Relational.Core.Entities;

namespace ArturRios.Heimdall.Domain.Entities;

/// <summary>
///     A scope-specific permission that can be created, read, updated, and deleted within a
///     <see cref="Scope" />. The <see cref="IncludeAsJwtClaim" /> flag controls whether the
///     permission's <see cref="Name" /> is folded into the acting identity's JWT as a claim at
///     login. A scope's permissions are managed by that scope's owners or a System Admin.
/// </summary>
public class ScopePermission : Entity
{
    /// <summary>
    ///     External identifier, generated on creation and used everywhere the permission is
    ///     addressed from outside the database (API paths, response bodies). The internal
    ///     <see cref="Entity.Id" /> is never exposed to callers.
    /// </summary>
    public Guid PublicId { get; set; } = Guid.NewGuid();

    /// <summary>Permission display name. Required, max 200 characters.</summary>
    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    /// <summary>Optional description of the permission's purpose. Max 500 characters.</summary>
    [MaxLength(500)]
    public string? Description { get; set; }

    /// <summary>
    ///     When <c>true</c>, this permission's <see cref="Name" /> is added as a claim on the JWT
    ///     issued to identities acting within the owning scope. Defaults to <c>false</c>.
    /// </summary>
    public bool IncludeAsJwtClaim { get; set; }

    /// <summary>Logical deletion flag. Logically deleted permissions are excluded from default queries.</summary>
    public bool IsDeleted { get; set; }

    /// <summary>Foreign key to the owning <see cref="Scope" /> (internal Id). Required.</summary>
    public long ScopeId { get; set; }

    /// <summary>Creation timestamp.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>Last update timestamp.</summary>
    public DateTime UpdatedAt { get; set; }

    // Navigation properties

    /// <summary>The scope this permission belongs to.</summary>
    public Scope Scope { get; set; } = null!;
}