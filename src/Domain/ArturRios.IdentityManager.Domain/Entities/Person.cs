using System.ComponentModel.DataAnnotations;
using ArturRios.Data.Relational.Core.Entities;

namespace ArturRios.IdentityManager.Domain.Entities;

/// <summary>
///     A registered identity. Has no direct scope attribute — its relationship to scopes is
///     derived from its role: a <c>User</c> belongs to exactly one scope (via <c>SCOPE_USER</c>),
///     a <c>ScopeAdmin</c> owns one or more scopes (via <c>SCOPE_OWNER</c>), and a
///     <c>SystemAdmin</c> belongs to no scope.
/// </summary>
public class Person : Entity
{
    /// <summary>
    ///     External identifier, generated on creation and used everywhere the person is addressed
    ///     from outside the database (API paths, response bodies, token claims). The internal
    ///     <see cref="Entity.Id" /> is never exposed to callers.
    /// </summary>
    public Guid PublicId { get; set; } = Guid.NewGuid();

    /// <summary>Full name. Required, max 200 characters.</summary>
    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    ///     Email address. Unique within the scope for <c>User</c>s (via <c>SCOPE_USER</c>);
    ///     unique system-wide for <c>ScopeAdmin</c>s and <c>SystemAdmin</c>s.
    /// </summary>
    [MaxLength(256)]
    public string Email { get; set; } = string.Empty;

    /// <summary>Hash computed from the password and <see cref="Salt" />.</summary>
    public byte[] PasswordHash { get; set; } = [];

    /// <summary>Randomly generated per person, used to hash the password.</summary>
    public byte[] Salt { get; set; } = [];

    /// <summary>Logical deletion flag. Logically deleted persons are excluded from default queries.</summary>
    public bool IsDeleted { get; set; }

    /// <summary>Email verification status.</summary>
    public bool EmailVerified { get; set; }

    /// <summary>Foreign key to the associated <c>Role</c> (internal Id). Required.</summary>
    public long RoleId { get; set; }

    /// <summary>Creation timestamp.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>Last update timestamp.</summary>
    public DateTime UpdatedAt { get; set; }

    // Navigation properties

    /// <summary>The role that classifies this person.</summary>
    public Role Role { get; set; } = null!;

    /// <summary>
    ///     Scope-ownership join rows. Non-empty for a <c>ScopeAdmin</c> (one per owned scope);
    ///     empty for a <c>User</c> or <c>SystemAdmin</c>.
    /// </summary>
    public ICollection<ScopeOwner> ScopeOwnerships { get; set; } = new List<ScopeOwner>();

    /// <summary>
    ///     Scope-membership join row. Present for a <c>User</c> (exactly one); <c>null</c> for a
    ///     <c>ScopeAdmin</c> or <c>SystemAdmin</c>.
    /// </summary>
    public ScopeUser? ScopeMembership { get; set; }

    /// <summary>Applications owned by this person.</summary>
    public ICollection<Application> OwnedApplications { get; set; } = new List<Application>();

    /// <summary>Password reset tokens issued for this person.</summary>
    public ICollection<PasswordResetToken> PasswordResetTokens { get; set; } = new List<PasswordResetToken>();

    /// <summary>Email verification tokens issued for this person.</summary>
    public ICollection<EmailVerificationToken> EmailVerificationTokens { get; set; } = new List<EmailVerificationToken>();
}
