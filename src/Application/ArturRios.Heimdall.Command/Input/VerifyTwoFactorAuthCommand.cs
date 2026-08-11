using ArturRios.Mediator.Command;

namespace ArturRios.Heimdall.Command.Input;

/// <summary>
///     Intent to complete a 2FA-gated login (UC-38) by redeeming the challenge token AF-11g issued at
///     login, together with either an app/email code or a recovery code. Carries no acting-person
///     identity — the caller holds no bearer token yet, only the challenge token, submitted here as
///     a plain request body field rather than an <c>Authorization</c> header (FR-2F-10) — the same
///     "opaque token as a body value" shape <see cref="ResetPasswordCommand" />'s <c>Token</c> uses.
///     The person the challenge names is resolved from <see cref="ChallengeToken" /> itself, inside
///     the handler.
/// </summary>
public class VerifyTwoFactorAuthCommand : BaseCommand
{
    /// <summary>The short-lived challenge token AF-11g returned from <c>POST /api/auth/login</c>.</summary>
    public string ChallengeToken { get; set; } = string.Empty;

    /// <summary>The caller's current app or email code, when proving a code rather than a recovery code.</summary>
    public string? Code { get; set; }

    /// <summary>One of the caller's unused recovery codes, when no code is available.</summary>
    public string? RecoveryCode { get; set; }
}
