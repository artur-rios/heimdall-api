using System.ComponentModel.DataAnnotations;
using ArturRios.Data.Relational.Core.Entities;

namespace ArturRios.Heimdall.Domain.Entities;

/// <summary>
///     A registered identity authenticated via Google Sign-In rather than a password. Always
///     equivalent to the <c>User</c> role and belongs to exactly one <see cref="Scope" />. Stored
///     in its own table with a direct <see cref="ScopeId" /> instead of going through the
///     owner/user join tables, since it never needs ownership or multi-scope semantics. Has no
///     <c>PasswordHash</c>, <c>Salt</c>, or <c>RoleId</c> — authentication is delegated to Google.
/// </summary>
public class GoogleUser : Entity
{
    /// <summary>
    ///     External identifier, generated on creation and used everywhere the Google User is
    ///     addressed from outside the database (API paths, response bodies, token claims). The
    ///     internal <see cref="Entity.Id" /> is never exposed to callers.
    /// </summary>
    public Guid PublicId { get; set; } = Guid.NewGuid();

    /// <summary>Google's stable <c>sub</c> claim. Required, unique within the scope.</summary>
    [MaxLength(255)]
    public string GoogleId { get; set; } = string.Empty;

    /// <summary>Full name, from Google's <c>name</c> claim. Max 200 characters.</summary>
    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    ///     Email address, from Google's <c>email</c> claim. Required, unique within the scope,
    ///     considered jointly with <c>User</c> persons' emails in that scope.
    /// </summary>
    [MaxLength(256)]
    public string Email { get; set; } = string.Empty;

    /// <summary>Email verification status, from Google's <c>email_verified</c> claim.</summary>
    public bool EmailVerified { get; set; }

    /// <summary>Optional profile picture URL, from Google's <c>picture</c> claim.</summary>
    [MaxLength(2048)]
    public string? ProfilePictureUrl { get; set; }

    /// <summary>Logical deletion flag. Logically deleted Google Users are excluded from default queries.</summary>
    public bool IsDeleted { get; set; }

    /// <summary>Foreign key to the associated <see cref="Scope" /> (internal Id). Required.</summary>
    public long ScopeId { get; set; }

    /// <summary>Creation timestamp.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>Last update timestamp.</summary>
    public DateTime UpdatedAt { get; set; }

    // Navigation properties

    /// <summary>The scope this Google User belongs to.</summary>
    public Scope Scope { get; set; } = null!;
}
