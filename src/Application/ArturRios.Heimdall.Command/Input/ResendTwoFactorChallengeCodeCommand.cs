using ArturRios.Mediator.Command;

namespace ArturRios.Heimdall.Command.Input;

/// <summary>
///     Intent to reissue the email code for a two-factor challenge that is still outstanding
///     (UC-46), so a person who never received the code UC-11 mailed can ask for another without
///     restarting sign-in. Carries no acting-person identity and no address: the caller holds no
///     bearer token yet, only the challenge token, submitted here as a plain request body field
///     rather than an <c>Authorization</c> header (FR-2F-10) — the same shape
///     <see cref="VerifyTwoFactorAuthCommand" /> uses. Whom the code is sent to is resolved from
///     <see cref="ChallengeToken" /> itself, inside the handler, so this can never be used to mail a
///     code to an address the caller chose.
/// </summary>
public class ResendTwoFactorChallengeCodeCommand : BaseCommand
{
    /// <summary>The short-lived challenge token AF-11g returned from <c>POST /api/auth/login</c>.</summary>
    public string ChallengeToken { get; set; } = string.Empty;
}
