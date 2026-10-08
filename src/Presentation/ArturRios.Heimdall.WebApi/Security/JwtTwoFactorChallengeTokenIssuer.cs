using ArturRios.Heimdall.Command.Services;
using ArturRios.Heimdall.Shared.Security;
using ArturRios.Jwt;
using ArturRios.Util.WebApi.Security.Authentication;
using ArturRios.Util.WebApi.Security.Interfaces;

namespace ArturRios.Heimdall.WebApi.Security;

/// <summary>
///     Issues and validates UC-38's short-lived challenge token as the application's own HMAC-signed
///     JWT (FR-2F-07…FR-2F-10, NFR-17), signed with the same secret as
///     <see cref="JwtAuthTokenIssuer" />'s full tokens but built and read independently of it, since
///     the two carry deliberately different claims.
/// </summary>
/// <remarks>
///     <para>
///         <b>Expiry.</b> Taken from <see cref="TwoFactorLifetimes.ChallengeToken" /> (10 minutes,
///         NFR-17) rather than added to <see cref="JwtConfiguration" /> as a second environment
///         variable: a full login token's lifetime is meant to be tuned per deployment, but a
///         challenge token's is a security property of the use case itself, so there is no
///         legitimate reason an operator would want it longer, and every reason to keep it fixed.
///     </para>
///     <para>
///         It is read from that shared type rather than written here because the number is not this
///         class's to choose. The App method would be served by a much shorter window — an
///         authenticator generates a code on demand, so its holder really can finish UC-38 within
///         moments of UC-11 returning the token. The Email method cannot: "within moments" is a
///         claim about someone else's mail infrastructure, and FR-2F-03 already committed to
///         tolerating ten minutes of it. Both methods share one challenge token, so its lifetime has
///         to be the longer of the two, and it has to be the <em>same</em> ten minutes the code it
///         was issued with gets — a shorter one silently retracted that tolerance and left a
///         correct, unexpired code with nowhere to be presented, since FR-2F-10 makes second-factor
///         verification the only endpoint that will take a challenge token.
///     </para>
///     <para>
///         <b>Claims.</b> Built directly rather than through <see cref="IdentityUserMapper" />'s
///         normal login path: the token carries the person's <c>PublicId</c>, their role (required by
///         <see cref="IAuthenticatedUserMapper.FromClaims" />, which cannot resolve an
///         <see cref="IdentityUser" /> without one), and <see cref="IdentityUser.MfaPending" /> set —
///         but never a <c>ScopeId</c>, <c>OwnedScopeIds</c>, or scope-permission claim, since
///         <see cref="IdentityUser" /> is constructed here with none of those populated. Carrying a
///         role at all is what lets <see cref="MfaPendingGuardFilter" /> actually authenticate and
///         then reject the token if it is misused as a bearer credential elsewhere (FR-2F-10) — a
///         token the claims mapper could not parse at all would already be rejected by
///         <c>AuthenticationMiddleware</c> before the filter ever ran, which would leave the filter
///         untested by construction rather than genuinely enforcing anything.
///     </para>
/// </remarks>
public class JwtTwoFactorChallengeTokenIssuer(
    JwtConfiguration configuration,
    JwtHandler jwtHandler,
    IAuthenticatedUserMapper mapper) : ITwoFactorChallengeTokenIssuer, ITwoFactorChallengeTokenValidator
{
    public Task<AuthToken> IssueAsync(Guid personId, int roleId, Guid challengeId)
    {
        var identity = new IdentityUser(personId, roleId) { MfaPending = true, ChallengeId = challengeId };

        var challengeConfiguration = configuration with
        {
            Claims = mapper.ToClaims(identity),
            ExpirationInSeconds = TwoFactorLifetimes.ChallengeToken.TotalSeconds
        };

        var token = jwtHandler.CreateToken(challengeConfiguration);

        return Task.FromResult(new AuthToken(token, DateTime.UtcNow.Add(TwoFactorLifetimes.ChallengeToken)));
    }

    /// <summary>
    ///     Signature and lifetime, against every configured key when there are any — the same rule
    ///     the bearer-token validator applies — so rotating the signing secret does not void the
    ///     challenges already in flight while it keeps every full token issued beside them valid.
    /// </summary>
    private Task<bool> IsSignedAndLiveAsync(string token) =>
        configuration.Keys.Count > 0
            ? jwtHandler.IsTokenValidAsync(token, configuration.Keys)
            : jwtHandler.IsTokenValidAsync(token, configuration.Secret);

    public async Task<TwoFactorChallengePrincipal?> ValidateAsync(string? token)
    {
        // Signature and lifetime (JwtHandler.IsTokenValidAsync validates both) — AF-38a's "expired
        // or invalid".
        if (string.IsNullOrWhiteSpace(token) || !await IsSignedAndLiveAsync(token))
        {
            return null;
        }

        var claims = TokenClaimsReader.Read(token);

        if (claims is null)
        {
            return null;
        }

        // FR-2F-10: only a token carrying the MFA-pending claim is a challenge token at all — a full
        // login token that happens to still be signature-valid is not accepted here either.
        // FR-2F-10 again: a challenge token names which challenge it is, so UC-38 can refuse one that
        // was already redeemed or has been superseded by a newer login. One without that claim
        // cannot be checked, and so is not accepted either.
        return mapper.FromClaims(claims) is IdentityUser { MfaPending: true, ChallengeId: { } challengeId } identity
            ? new TwoFactorChallengePrincipal(identity.Id, challengeId)
            : null;
    }
}
