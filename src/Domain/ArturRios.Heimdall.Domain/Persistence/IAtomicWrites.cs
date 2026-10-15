using ArturRios.Heimdall.Domain.Entities;

namespace ArturRios.Heimdall.Domain.Persistence;

/// <summary>
///     The writes that concurrent requests compete for: spending something single-use, and charging
///     a bounded budget. Each is one conditional <c>UPDATE … WHERE</c> evaluated by the database, so of
///     any number of simultaneous requests exactly as many succeed as the condition allows — one, for
///     a single-use item.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why this exists.</b> The repositories write whole rows: read, change in memory, save.
///         Two requests that read the same row before either saves both see "unused", "under the
///         cap" or "not yet redeemed", and both act on it — one recovery code redeemed twice, one
///         challenge traded for two tokens, ten wrong passwords counted as one. Moving the check into
///         the <c>WHERE</c> clause of the write that spends it closes that window, because the
///         database applies the two together.
///     </para>
///     <para>
///         <b>Charge first, then check.</b> Every budget here — login attempts (FR-AU-09), guesses at
///         an email code (FR-2F-13) or at a challenge, guesses at the password of a pending
///         registration (FR-SR-09), reissues of a challenge's code (FR-2F-16) — is reserved before the
///         expensive or secret comparison runs, not counted after it fails.
///         Counting afterwards let a burst of parallel guesses all pass the "under the cap" check
///         before any of them was counted; reserving first means no more comparisons can ever run
///         than the cap allows.
///     </para>
///     <para>
///         <b>Writes beside a revocation.</b> An application credential is revoked by one request
///         (UC-55) while another may be using it (UC-56). A whole-row save of either request's copy
///         would write <c>revoked_at</c> back as that request read it, and silently undo or overwrite
///         the revocation. So both the revocation and the use stamp are targeted, conditional writes
///         here: the first revocation stands, and a use stamp only lands while the credential is
///         still live.
///     </para>
///     <para>
///         Every method takes the entity the caller already holds and, when the write succeeds,
///         brings that instance's changed properties in line with what was written, so the caller
///         and anything it saves later see the same values the database does.
///     </para>
/// </remarks>
public interface IAtomicWrites
{
    /// <summary>
    ///     FR-AU-09: reserves one login attempt — increments the failure counter, but only while the
    ///     account is not locked out at <paramref name="now" /> and the counter is below
    ///     <paramref name="maxAttempts" />. <c>false</c> means no attempt may be made.
    /// </summary>
    /// <remarks>
    ///     The attempt that brings the counter to <paramref name="maxAttempts" /> also locks the
    ///     account until <paramref name="lockedOutUntil" /> and resets the counter, in the same
    ///     write. The lock therefore does not depend on the caller surviving to compare the
    ///     password: a request that reserves the last attempt and then fails leaves an ordinary
    ///     lock that expires on its own. A row already at or past the limit with no lockout date is
    ///     admitted and locked the same way, so it cannot stay stuck. On success the instance's
    ///     counter and lockout are set to match, from the value this request read, which under
    ///     concurrent attempts can trail the stored count.
    /// </remarks>
    Task<bool> TryReserveLoginAttemptAsync(Person person, int maxAttempts, DateTime now, DateTime lockedOutUntil);

    /// <summary>
    ///     FR-SR-09: reserves one guess at the sign-up password of a pending registration, while it has
    ///     had fewer than <paramref name="maxGuesses" />. <c>false</c> means no comparison may be made.
    /// </summary>
    /// <remarks>
    ///     On success the instance's <c>FailedAttempts</c> is set to the value this request read plus one,
    ///     which under concurrent guesses can trail the stored count. A caller that needs the stored count
    ///     (to tell whether this was the last guess) must ask the database for it.
    /// </remarks>
    Task<bool> TryChargePendingRegistrationGuessAsync(PendingRegistration pending, int maxGuesses);

    /// <summary>FR-AU-09: after a correct password, clears the counter and any lockout.</summary>
    Task ClearLoginAttemptsAsync(Person person);

    /// <summary>
    ///     FR-2F-10: makes <paramref name="challengeId" /> the configuration's one outstanding
    ///     challenge, with all of its reissues (FR-2F-16) and guesses available.
    /// </summary>
    Task StartChallengeAsync(TwoFactorAuth configuration, Guid challengeId);

    /// <summary>
    ///     Reserves one guess at the outstanding challenge <paramref name="challengeId" />, while it is
    ///     still outstanding and has had fewer than <paramref name="maxGuesses" />.
    /// </summary>
    Task<bool> TryChargeChallengeGuessAsync(TwoFactorAuth configuration, Guid challengeId, int maxGuesses);

    /// <summary>
    ///     After a wrong guess, retires the challenge — clears it, so its token is refused from then on
    ///     — if it has had <paramref name="maxGuesses" />.
    /// </summary>
    Task ExpireChallengeIfExhaustedAsync(TwoFactorAuth configuration, Guid challengeId, int maxGuesses);

    /// <summary>FR-2F-10: redeems the outstanding challenge <paramref name="challengeId" />. Once.</summary>
    Task<bool> TryRedeemChallengeAsync(TwoFactorAuth configuration, Guid challengeId);

    /// <summary>
    ///     FR-2F-16: reserves one reissue of the outstanding challenge's email code, while it has had
    ///     fewer than <paramref name="maxReissues" />.
    /// </summary>
    Task<bool> TryChargeEmailCodeReissueAsync(TwoFactorAuth configuration, Guid challengeId, int maxReissues);

    /// <summary>
    ///     FR-2F-14: records <paramref name="timeStep" /> as the last accepted TOTP step, if it is later
    ///     than the one recorded. <c>false</c> means the code was already used, or a later one has been.
    /// </summary>
    Task<bool> TryAdvanceTotpStepAsync(TwoFactorAuth configuration, long timeStep, DateTime now);

    /// <summary>
    ///     FR-2F-13: reserves one guess at an email code that is unused, unexpired at
    ///     <paramref name="now" />, and has had fewer than <paramref name="maxGuesses" />.
    /// </summary>
    Task<bool> TryChargeEmailCodeGuessAsync(TwoFactorEmailCode code, int maxGuesses, DateTime now);

    /// <summary>FR-2F-13: after a wrong guess, retires the code if it has had <paramref name="maxGuesses" />.</summary>
    Task RetireEmailCodeIfExhaustedAsync(TwoFactorEmailCode code, int maxGuesses);

    /// <summary>Spends an email code. Once.</summary>
    Task<bool> TryConsumeEmailCodeAsync(TwoFactorEmailCode code);

    /// <summary>Spends a recovery code (UC-38, UC-40). Once.</summary>
    Task<bool> TryConsumeRecoveryCodeAsync(TwoFactorRecoveryCode code, DateTime usedAt);

    /// <summary>UC-13: spends a password reset token that is unexpired at <paramref name="now" />. Once.</summary>
    Task<bool> TryConsumePasswordResetTokenAsync(PasswordResetToken token, DateTime now);

    /// <summary>UC-14: spends an email verification token that is unexpired at <paramref name="now" />. Once.</summary>
    Task<bool> TryConsumeEmailVerificationTokenAsync(EmailVerificationToken token, DateTime now);

    /// <summary>
    ///     UC-56: records a token exchange — sets <c>LastUsedAt</c> to <paramref name="now" /> and writes
    ///     no other column — while the credential is still live at <paramref name="now" />
    ///     (<see cref="ApplicationCredential.LiveAt" />). <c>false</c> means it was revoked or expired
    ///     after the caller read it, and no token may be issued from it.
    /// </summary>
    Task<bool> TryStampCredentialUseAsync(ApplicationCredential credential, DateTime now);

    /// <summary>
    ///     UC-55: revokes a credential at <paramref name="revokedAt" />. Once: <c>false</c> means it was
    ///     already revoked, possibly after the caller read it, and the first revocation's time stands
    ///     (AF-55d).
    /// </summary>
    Task<bool> TryRevokeCredentialAsync(ApplicationCredential credential, DateTime revokedAt);

    /// <summary>
    ///     FR-AU-11: gives the person a fresh <see cref="Person.SecurityStamp" />, writing no other
    ///     column, so every token issued before this call is refused from the next request on.
    /// </summary>
    /// <remarks>
    ///     Unconditional, unlike the writes above: any number of rotations may race and every one of
    ///     them revokes the tokens issued before it. What has to be targeted is the write itself. A
    ///     whole-row save of a copy read before the rotation would put the old stamp back and revive
    ///     what it revoked, which is why the mapping never lets such a save write this column.
    /// </remarks>
    Task RotateSecurityStampAsync(Person person);
}
