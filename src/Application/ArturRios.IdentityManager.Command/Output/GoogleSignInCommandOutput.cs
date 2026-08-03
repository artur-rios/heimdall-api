using ArturRios.Mediator.Command;

namespace ArturRios.IdentityManager.Command.Output;

/// <summary>
///     Result of <see cref="Input.GoogleSignInCommand" /> (UC-25): the issued authentication token
///     and when it expires. The same shape as <see cref="LoginCommandOutput" />, because a Google
///     User ends up holding the same kind of token a password login yields — the claims carry the
///     Google User's and the scope's <c>PublicId</c>s (NFR-15), so nothing about the account is
///     repeated here.
/// </summary>
/// <remarks>
///     Says nothing about whether this call signed the account up or signed it in. UC-25 specifies
///     one response for both, and a caller has no use for the distinction: either way they are
///     authenticated, and the Google User row exists.
/// </remarks>
public class GoogleSignInCommandOutput : CommandOutput
{
    /// <summary>The signed authentication token, to be sent as a bearer token.</summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>When the token expires, in UTC.</summary>
    public DateTime ExpiresAt { get; set; }
}
