using ArturRios.Heimdall.Domain.Entities;

namespace ArturRios.Heimdall.Command.Services;

/// <summary>
///     The result of <see cref="ITwoFactorFactorVerifier.VerifyAsync" />: whether the submitted code
///     or recovery code matched, and — when it did — which row (if any) the caller must mark
///     consumed. At most one of <see cref="ConsumedEmailCode" />/<see cref="ConsumedRecoveryCode" />
///     is ever set, since a match is either an app code (neither), an email code, or a recovery code.
/// </summary>
public sealed record TwoFactorFactorVerificationResult(
    bool Matched,
    TwoFactorEmailCode? ConsumedEmailCode,
    TwoFactorRecoveryCode? ConsumedRecoveryCode)
{
    public static readonly TwoFactorFactorVerificationResult NoMatch = new(false, null, null);

    public static TwoFactorFactorVerificationResult AppCodeMatch { get; } = new(true, null, null);

    public static TwoFactorFactorVerificationResult ForEmailCode(TwoFactorEmailCode emailCode) =>
        new(true, emailCode, null);

    public static TwoFactorFactorVerificationResult ForRecoveryCode(TwoFactorRecoveryCode recoveryCode) =>
        new(true, null, recoveryCode);
}

/// <summary>
///     Verifies a submitted second factor — a TOTP/email code, or a recovery code — against a
///     person's <see cref="TwoFactorAuth" /> configuration. Extracted out of
///     <c>VerifyTwoFactorAuthCommandHandler</c> (UC-38) so the same "code against TOTP, or against
///     the current email code, or against an unused recovery code" comparison is written exactly
///     once and reused wherever else a second factor must be proven — <c>DisableTwoFactorAuthCommandHandler</c>
///     (UC-39) and <c>RegenerateRecoveryCodesCommandHandler</c> (UC-40).
/// </summary>
public interface ITwoFactorFactorVerifier
{
    /// <summary>
    ///     Checks <paramref name="recoveryCode" /> against an unused, matching recovery code when it
    ///     is supplied; otherwise checks <paramref name="code" /> against a current TOTP code (when
    ///     <see cref="TwoFactorAuth.AppEnabled" />) and, failing that, against a live email code
    ///     (when <see cref="TwoFactorAuth.EmailEnabled" />). Writes only what guessing itself costs —
    ///     an accepted TOTP step (FR-2F-14) and a charged email-code guess (FR-2F-13). Spending the
    ///     returned email or recovery code is the caller's, once every other check for its own use
    ///     case has also passed, and is done through <c>IAtomicWrites</c> so only one request can.
    /// </summary>
    Task<TwoFactorFactorVerificationResult> VerifyAsync(
        TwoFactorAuth twoFactorAuth, string? code, string? recoveryCode);
}
