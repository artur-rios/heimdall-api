using ArturRios.Mediator.Command;

namespace ArturRios.Heimdall.Command.Input;

/// <summary>
///     Intent to confirm an email address using the token UC-06 mailed out (UC-14, FR-EV-03). Carries
///     no email and no scope: the token identifies the person on its own, which is why it is 48
///     random characters long.
/// </summary>
public class VerifyEmailCommand : BaseCommand
{
    /// <summary>The verification token, exactly as it was issued.</summary>
    public string Token { get; set; } = string.Empty;
}
