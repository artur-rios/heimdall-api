using ArturRios.Mediator.Command;

namespace ArturRios.Heimdall.Command.Output;

/// <summary>
///     Result of <see cref="Input.VerifyTwoFactorAuthCommand" /> (UC-38): the full authentication
///     token issued once a second factor checked out (FR-2F-09) — the same shape
///     <see cref="LoginCommandOutput" />'s direct-login success case returns, since a UC-38 login
///     ends exactly like an ungated one.
/// </summary>
public class VerifyTwoFactorAuthCommandOutput : CommandOutput
{
    /// <summary>The signed authentication token, to be sent as a bearer token.</summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>When the token expires, in UTC.</summary>
    public DateTime ExpiresAt { get; set; }

    /// <summary>
    ///     Whether the authenticated person's email address is verified (FR-EV-05), reported here
    ///     rather than on UC-11's challenge response — the same value a direct login returns, since a
    ///     UC-38 login ends exactly like an ungated one.
    /// </summary>
    public bool EmailVerified { get; set; }
}
