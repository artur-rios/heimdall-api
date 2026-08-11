using ArturRios.Heimdall.Command.Services;
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
///         <b>Expiry.</b> Hardcoded to <see cref="ChallengeTokenLifetime" /> (5 minutes, NFR-17's
///         target) rather than added to <see cref="JwtConfiguration" /> as a second environment
///         variable: a full login token's lifetime is meant to be tuned per deployment, but a
///         challenge token's is a security property of the use case itself — a caller is expected to
///         finish UC-38 within moments of UC-11 returning it — so there is no legitimate reason an
///         operator would want it longer, and every reason to keep it fixed and short.
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
    private static readonly TimeSpan ChallengeTokenLifetime = TimeSpan.FromMinutes(5);

    public Task<AuthToken> IssueAsync(Guid personId, int roleId)
    {
        var identity = new IdentityUser(personId, roleId) { MfaPending = true };

        var challengeConfiguration = configuration with
        {
            Claims = mapper.ToClaims(identity), ExpirationInSeconds = ChallengeTokenLifetime.TotalSeconds
        };

        var token = jwtHandler.CreateToken(challengeConfiguration);

        return Task.FromResult(new AuthToken(token, DateTime.UtcNow.Add(ChallengeTokenLifetime)));
    }

    public async Task<TwoFactorChallengePrincipal?> ValidateAsync(string? token)
    {
        // Signature and lifetime (JwtHandler.IsTokenValidAsync validates both) — AF-38a's "expired
        // or invalid".
        if (string.IsNullOrWhiteSpace(token) ||
            !await jwtHandler.IsTokenValidAsync(token, configuration.Secret))
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
        return mapper.FromClaims(claims) is IdentityUser { MfaPending: true } identity
            ? new TwoFactorChallengePrincipal(identity.Id)
            : null;
    }
}
