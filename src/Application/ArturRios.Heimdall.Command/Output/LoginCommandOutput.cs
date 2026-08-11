using ArturRios.Mediator.Command;

namespace ArturRios.Heimdall.Command.Output;

/// <summary>
///     Result of <see cref="Input.LoginCommand" /> (UC-11): the issued authentication token and when
///     it expires (FR-AU-03). The token's own claims carry the person's and scopes' <c>PublicId</c>s
///     (FR-AU-04); nothing about the person is repeated here.
/// </summary>
public class LoginCommandOutput : CommandOutput
{
    /// <summary>The signed authentication token, to be sent as a bearer token.</summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>When the token expires, in UTC.</summary>
    public DateTime ExpiresAt { get; set; }
}
