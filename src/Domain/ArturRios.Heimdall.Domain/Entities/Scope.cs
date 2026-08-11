using System.ComponentModel.DataAnnotations;
using ArturRios.Data.Relational.Core.Entities;

namespace ArturRios.Heimdall.Domain.Entities;

/// <summary>
///     A logical tenant boundary that groups the owners, users, applications, and Google Users
///     belonging to a specific client system.
/// </summary>
public class Scope : Entity
{
    /// <summary>
    ///     External identifier, generated on creation and used everywhere the scope is addressed
    ///     from outside the database (API paths, response bodies, token claims). The internal
    ///     <see cref="Entity.Id" /> is never exposed to callers.
    /// </summary>
    public Guid PublicId { get; set; } = Guid.NewGuid();

    /// <summary>Scope display name. Required, unique.</summary>
    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    /// <summary>Optional description of the scope's purpose.</summary>
    [MaxLength(500)]
    public string? Description { get; set; }

    /// <summary>Logical deletion flag. Logically deleted scopes are excluded from default queries.</summary>
    public bool IsDeleted { get; set; }

    /// <summary>
    ///     Whether Google sign-up/sign-in is available for this scope. Defaults to <c>false</c>;
    ///     only the scope's owners or a System Admin may toggle it.
    /// </summary>
    public bool GoogleSignInEnabled { get; set; }

    /// <summary>Creation timestamp.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>Last update timestamp.</summary>
    public DateTime UpdatedAt { get; set; }

    // Navigation properties

    /// <summary>Ownership join rows linking this scope to its owners (persons with the <c>ScopeAdmin</c> role).</summary>
    public ICollection<ScopeOwner> Owners { get; set; } = new List<ScopeOwner>();

    /// <summary>Membership join rows linking this scope to its users (persons with the <c>User</c> role).</summary>
    public ICollection<ScopeUser> Users { get; set; } = new List<ScopeUser>();

    /// <summary>Applications contained in this scope.</summary>
    public ICollection<Application> Applications { get; set; } = new List<Application>();

    /// <summary>Google Users contained in this scope.</summary>
    public ICollection<GoogleUser> GoogleUsers { get; set; } = new List<GoogleUser>();

    /// <summary>Scope-specific permissions defined within this scope.</summary>
    public ICollection<ScopePermission> ScopePermissions { get; set; } = new List<ScopePermission>();
}
