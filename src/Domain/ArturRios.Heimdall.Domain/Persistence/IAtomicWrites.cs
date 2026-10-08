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
///         an email code (FR-2F-13) or at a challenge, reissues of a challenge's code (FR-2F-16) — is
///         reserved before the expensive or secret comparison runs, not counted after it fails.
///         Counting afterwards let a burst of parallel guesses all pass the "under the cap" check
///         before any of them was counted; reserving first means no more comparisons can ever run
///         than the cap allows.
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
    Task<bool> TryReserveLoginAttemptAsync(Person person, int maxAttempts, DateTime now);

    /// <summary>
    ///     FR-AU-09: after a wrong password, locks the account until <paramref name="lockedOutUntil" />
    ///     and resets the counter — if, and only if, the counter has reached <paramref name="maxAttempts" />.
    /// </summary>
    Task LockOutIfExhaustedAsync(Person person, int maxAttempts, DateTime lockedOutUntil);

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
}
