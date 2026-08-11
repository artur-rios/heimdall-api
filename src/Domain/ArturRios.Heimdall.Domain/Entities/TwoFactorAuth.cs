using ArturRios.Data.Relational.Core.Entities;

namespace ArturRios.Heimdall.Domain.Entities;

/// <summary>
///     A person's two-factor authentication configuration (UC-36 – UC-40, FR-2F-01…FR-2F-12). At
///     most one row per person. <see cref="IsActive" /> distinguishes a confirmed configuration from
///     one still pending confirmation (UC-36 created it, UC-37 has not yet activated it). Uses only
///     an internal <c>Id</c> — never addressed by ID in a path; a person's own configuration is
///     reached implicitly through their authenticated identity (see §4.1 of the System Requirements
///     Document).
/// </summary>
public class TwoFactorAuth : Entity
{
    /// <summary>Foreign key to the owning <see cref="Person" /> (internal Id). Required, unique.</summary>
    public long PersonId { get; set; }

    /// <summary>Whether the authenticator-app method is configured (FR-2F-02).</summary>
    public bool AppEnabled { get; set; }

    /// <summary>Whether the email method is configured (FR-2F-03).</summary>
    public bool EmailEnabled { get; set; }

    /// <summary>
    ///     The TOTP secret, encrypted at rest via <c>ITotpSecretProtector</c>. Present only when
    ///     <see cref="AppEnabled" /> is <c>true</c>; the plaintext secret is never stored.
    /// </summary>
    public byte[]? TotpSecretEncrypted { get; set; }

    /// <summary>
    ///     Set <c>true</c> only once every method selected at setup has been confirmed (UC-37,
    ///     FR-2F-04). <c>false</c> with a row present means setup was initiated (UC-36) but not yet
    ///     confirmed.
    /// </summary>
    public bool IsActive { get; set; }

    /// <summary>Creation timestamp.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>Last update timestamp.</summary>
    public DateTime UpdatedAt { get; set; }

    // Navigation properties

    /// <summary>The person this configuration belongs to.</summary>
    public Person Person { get; set; } = null!;

    /// <summary>Outstanding email codes issued for this configuration (UC-36 step 4, UC-37 step 2).</summary>
    public ICollection<TwoFactorEmailCode> EmailCodes { get; set; } = new List<TwoFactorEmailCode>();

    /// <summary>Recovery codes issued for this configuration (UC-37 step 4, UC-38, UC-40).</summary>
    public ICollection<TwoFactorRecoveryCode> RecoveryCodes { get; set; } = new List<TwoFactorRecoveryCode>();
}
