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
public class TwoFactorAuth : Entity<long>
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

    /// <summary>
    ///     The TOTP time step (30-second counter since the Unix epoch) of the most recently accepted
    ///     authenticator-app code, or <c>null</c> while none has been accepted. A code is refused
    ///     unless its step is strictly greater, which is what makes an app code single-use: without
    ///     it the same six digits verify for as long as they are current, and the verification
    ///     window widens that to a step either side (FR-2F-14, RFC 6238 §5.2).
    /// </summary>
    public long? LastTotpTimeStepUsed { get; set; }

    /// <summary>
    ///     How many times the current challenge has had its email code reissued (UC-46, FR-2F-13).
    ///     Reset to zero every time UC-11 issues a challenge, so the cap counts reissues per
    ///     authentication attempt rather than per account — a fresh budget costs a fresh password
    ///     check, which is the price FR-2F-13 charged before a resend existed.
    /// </summary>
    /// <remarks>
    ///     Kept here rather than as a claim on the challenge token because UC-46 must never return a
    ///     new token: one could only be returned for a genuine challenge, so returning it would tell
    ///     an anonymous caller that the challenge they presented was real. A claim that cannot be
    ///     reissued cannot be incremented, which leaves the count on the server.
    /// </remarks>
    public int EmailCodeReissueCount { get; set; }

    /// <summary>
    ///     Identifies the one challenge UC-11 most recently issued for this configuration and UC-38 has
    ///     not yet redeemed, or <c>null</c> when none is outstanding. The challenge token carries the
    ///     same value as a claim, and is honoured only while the two agree (FR-2F-10).
    /// </summary>
    /// <remarks>
    ///     A signature and an expiry say a challenge token is genuine and recent; neither says it has
    ///     not already been spent. Without this a redeemed challenge stayed redeemable for the rest of
    ///     its ten minutes, so whoever held it could trade any further factor for another full token
    ///     without the password. Redeeming clears it, and a new login replaces it, so only the latest
    ///     unredeemed challenge is ever accepted — the same "only the newest is live" rule the email
    ///     codes issued alongside it already follow.
    /// </remarks>
    public Guid? ChallengeId { get; set; }

    /// <summary>
    ///     Guesses made at the outstanding challenge with an authenticator-app code or a recovery code.
    ///     Reset with <see cref="ChallengeId" /> whenever UC-11 issues a challenge; at the cap the
    ///     challenge is cleared, so further guessing costs a fresh password check.
    /// </summary>
    /// <remarks>
    ///     The email code has always had its own cap (FR-2F-13): five wrong guesses retire it. An app
    ///     code and a recovery code had none — only the per-address rate limit — so a challenge could
    ///     be guessed at for its whole ten minutes from as many addresses as an attacker could muster.
    ///     This gives them the same five, counted on the challenge because neither has a row of its
    ///     own to count on: a TOTP code is computed, and a guessed recovery code matches no row.
    /// </remarks>
    public int ChallengeAttempts { get; set; }

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
