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
///     (UC-39) today, and UC-40's regeneration later.
/// </summary>
public interface ITwoFactorFactorVerifier
{
    /// <summary>
    ///     Checks <paramref name="recoveryCode" /> against an unused, matching recovery code when it
    ///     is supplied; otherwise checks <paramref name="code" /> against a current TOTP code (when
    ///     <see cref="TwoFactorAuth.AppEnabled" />) and, failing that, against a live email code
    ///     (when <see cref="TwoFactorAuth.EmailEnabled" />). Does not write anything — the caller
    ///     decides whether and how to mark the returned row as used, once every other check for its
    ///     own use case has also passed.
    /// </summary>
    Task<TwoFactorFactorVerificationResult> VerifyAsync(
        TwoFactorAuth twoFactorAuth, string? code, string? recoveryCode);
}
