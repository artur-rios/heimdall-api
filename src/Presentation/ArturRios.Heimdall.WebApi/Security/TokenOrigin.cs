using Microsoft.IdentityModel.JsonWebTokens;

namespace ArturRios.Heimdall.WebApi.Security;

/// <summary>
///     Whether a token names Heimdall as its issuer and one of the audiences Heimdall issues it
///     under — the check every reader of a Heimdall token makes beside the signature and lifetime.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why it exists.</b> <c>ArturRios.Jwt</c> checks only the signature and lifetime, and a
///         signature does not say whose token it is. Heimdall's ES256 key signs every kind of token it
///         issues, so a valid signature alone would let one kind pass for another; and during the
///         overlap the readers also accept the legacy HS256 secret, which is shared with other
///         services (Threat Model TB-5), so a token signed with it for another service's audience, or
///         under another issuer, would be read here as one of Heimdall's own. Each reader names the
///         audiences it takes: the bearer-token validator the three person audiences
///         (<see cref="PersonTokenAudiences" />), the challenge reader the challenge audience alone,
///         and the application-token reader the application audience alone.
///     </para>
///     <para>
///         <b>Exact.</b> The issuer must equal the configured one, ordinally; a token without an
///         <c>iss</c> passes only when none is configured, which is how Heimdall then issues. A token
///         must carry exactly one audience, because Heimdall never issues one with several; no
///         <c>aud</c> at all counts as the empty audience, again how Heimdall issues when none is
///         configured. Run it only on a token whose signature has been checked: before that, the
///         claims are whatever the sender wrote.
///     </para>
/// </remarks>
public static class TokenOrigin
{
    /// <summary>
    ///     Whether <paramref name="token" /> was issued by <paramref name="issuer" /> for exactly one
    ///     of <paramref name="audiences" />. An unreadable token is not.
    /// </summary>
    public static bool IsIssuedFor(string token, string issuer, params IReadOnlyCollection<string> audiences)
    {
        try
        {
            var jwt = new JsonWebToken(token);
            var tokenAudiences = jwt.Audiences.ToList();

            var audience = tokenAudiences.Count switch
            {
                0 => string.Empty,
                1 => tokenAudiences[0],
                _ => null
            };

            return audience is not null &&
                   string.Equals(jwt.Issuer ?? string.Empty, issuer, StringComparison.Ordinal) &&
                   audiences.Contains(audience, StringComparer.Ordinal);
        }
        catch (Exception)
        {
            // Total, like the claims reader: a token whose header, issuer or audience cannot be read
            // is refused as unauthenticated, never failed with a 500.
            return false;
        }
    }
}
