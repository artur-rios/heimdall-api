using System.ComponentModel.DataAnnotations;
using ArturRios.Data.Relational.Core.Entities;

namespace ArturRios.Heimdall.Domain.Entities;

/// <summary>
///     A time-limited, single-use token issued for a password reset. Never addressed by its
///     internal <see cref="Entity.Id" />; the caller-facing reference is the random token string
///     delivered by email, so it has no <c>PublicId</c>.
/// </summary>
public class PasswordResetToken : Entity
{
    /// <summary>Foreign key to the associated <see cref="Person" /> (internal Id). Required.</summary>
    public long PersonId { get; set; }

    /// <summary>
    ///     SHA-256 of the token that was emailed, as lowercase hex. The token itself is never stored: it replaces a
    ///     password, so a reader of this table would otherwise be able to complete a reset for any
    ///     account holding a live one (Threat Model TH-14). Written and compared through
    ///     <c>SingleUseTokenHash</c>.
    /// </summary>
    [MaxLength(64)]
    public string TokenHash { get; set; } = string.Empty;

    /// <summary>Expiration timestamp, after which the token is rejected.</summary>
    public DateTime ExpiresAt { get; set; }

    /// <summary>Whether the token has already been consumed.</summary>
    public bool Used { get; set; }

    // Navigation properties

    /// <summary>The person this token was issued for.</summary>
    public Person Person { get; set; } = null!;
}
