using ArturRios.Data.Relational.Core.Entities;

namespace ArturRios.Heimdall.Domain.Entities;

/// <summary>
///     A time-limited, single-use numeric code issued for the email two-factor method (UC-36 step 4,
///     FR-2F-03). Not part of the System Requirements Document's data model — an implementation
///     detail of how the email method's pending code is tracked between UC-36 (issue) and UC-37
///     (confirm), shaped after <see cref="PasswordResetToken" />. Never addressed by ID; a person's
///     current code is reached through their <see cref="TwoFactorAuth" /> configuration.
/// </summary>
public class TwoFactorEmailCode : Entity
{
    /// <summary>
    ///     Foreign key to the owning <see cref="TwoFactorAuth" /> configuration (internal Id).
    ///     Required.
    /// </summary>
    public long TwoFactorAuthId { get; set; }

    /// <summary>Hash computed from the 6-digit code and <see cref="Salt" />.</summary>
    public byte[] CodeHash { get; set; } = [];

    /// <summary>Randomly generated per code, used to hash it.</summary>
    public byte[] Salt { get; set; } = [];

    /// <summary>Expiration timestamp, 10 minutes after issue (FR-2F-03).</summary>
    public DateTime ExpiresAt { get; set; }

    /// <summary>Whether the code has already been consumed or superseded by a fresher one.</summary>
    public bool Used { get; set; }

    /// <summary>Creation timestamp.</summary>
    public DateTime CreatedAt { get; set; }

    // Navigation properties

    /// <summary>The two-factor configuration this code was issued for.</summary>
    public TwoFactorAuth TwoFactorAuth { get; set; } = null!;
}
