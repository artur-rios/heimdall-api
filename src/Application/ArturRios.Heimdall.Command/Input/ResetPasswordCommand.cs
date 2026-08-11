using ArturRios.Mediator.Command;

namespace ArturRios.Heimdall.Command.Input;

/// <summary>
///     Intent to replace a password using the token UC-12 mailed out (UC-13, FR-PR-03). Carries no
///     email and no scope id: the token identifies the person on its own, which is why UC-12 made it
///     long and random.
/// </summary>
public class ResetPasswordCommand : BaseCommand
{
    /// <summary>The reset token, exactly as it was issued.</summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>The password to set.</summary>
    public string NewPassword { get; set; } = string.Empty;
}
