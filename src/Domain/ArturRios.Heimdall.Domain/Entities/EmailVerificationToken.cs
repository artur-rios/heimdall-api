using System.ComponentModel.DataAnnotations;
using ArturRios.Data.Relational.Core.Entities;

namespace ArturRios.Heimdall.Domain.Entities;

/// <summary>
///     A time-limited, single-use token issued to verify a person's email address. Never addressed
///     by its internal <see cref="Entity.Id" />; the caller-facing reference is the random token
///     string delivered by email, so it has no <c>PublicId</c>.
/// </summary>
public class EmailVerificationToken : Entity
{
    /// <summary>Foreign key to the associated <see cref="Person" /> (internal Id). Required.</summary>
    public long PersonId { get; set; }

    /// <summary>
    ///     SHA-256 of the token that was emailed, as lowercase hex, for the same reason as on
    ///     <see cref="PasswordResetToken" /> (Threat Model TH-14): a verification token confirms an
    ///     address, and an address is what a password reset is addressed to. Written and compared
    ///     through <c>SingleUseTokenHash</c>.
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
