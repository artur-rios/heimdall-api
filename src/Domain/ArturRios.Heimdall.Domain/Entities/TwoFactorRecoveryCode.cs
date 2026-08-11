using ArturRios.Data.Relational.Core.Entities;

namespace ArturRios.Heimdall.Domain.Entities;

/// <summary>
///     A single-use recovery code for a person's two-factor authentication configuration (UC-37 step
///     4, FR-2F-05). Ten rows are created together when setup is confirmed, and again — replacing the
///     previous ten — when UC-40 regenerates them. The plaintext code is returned to the caller
///     exactly once, in the confirmation (or regeneration) response; only its hash is ever persisted
///     (see §4.10 of the System Requirements Document). Never addressed by ID; reached through the
///     owning <see cref="TwoFactorAuth" /> configuration.
/// </summary>
public class TwoFactorRecoveryCode : Entity
{
    /// <summary>
    ///     Foreign key to the owning <see cref="TwoFactorAuth" /> configuration (internal Id).
    ///     Required.
    /// </summary>
    public long TwoFactorAuthId { get; set; }

    /// <summary>
    ///     Hash of the recovery code. §4.10 documents no per-code salt column — unlike
    ///     <see cref="TwoFactorEmailCode" />, these are high-entropy random strings rather than
    ///     user-chosen secrets, so a keyed/plain one-way hash is enough to keep the plaintext
    ///     unrecoverable at rest.
    /// </summary>
    public byte[] CodeHash { get; set; } = [];

    /// <summary>Whether the code has already been consumed (UC-38).</summary>
    public bool Used { get; set; }

    /// <summary>Timestamp the code was consumed; null until then.</summary>
    public DateTime? UsedAt { get; set; }

    /// <summary>Creation timestamp.</summary>
    public DateTime CreatedAt { get; set; }

    // Navigation properties

    /// <summary>The two-factor configuration this code was issued for.</summary>
    public TwoFactorAuth TwoFactorAuth { get; set; } = null!;
}
