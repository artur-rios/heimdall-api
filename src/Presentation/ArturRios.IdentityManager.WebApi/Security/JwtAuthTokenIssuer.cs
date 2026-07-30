using ArturRios.IdentityManager.Command.Services;
using ArturRios.Jwt;
using ArturRios.Util.WebApi.Security.Interfaces;

namespace ArturRios.IdentityManager.WebApi.Security;

/// <summary>
///     Issues UC-11's authentication token as the application's own HMAC-signed JWT (FR-AU-03),
///     using the same <see cref="IAuthenticatedUserMapper" /> the middleware validates incoming
///     tokens with — so a token this class issues is one the API can always read back.
/// </summary>
/// <remarks>
///     Expiry comes from the registered <see cref="JwtConfiguration" />, which reads it from the
///     environment (NFR-03), and is reported to the caller so a client need not decode the token to
///     know when to re-authenticate.
/// </remarks>
public class JwtAuthTokenIssuer(
    JwtConfiguration configuration,
    JwtHandler jwtHandler,
    IAuthenticatedUserMapper mapper) : IAuthTokenIssuer
{
    public AuthToken Issue(AuthTokenSubject subject)
    {
        var user = new IdentityUser(
            subject.PersonId, subject.RoleId, subject.ScopeId, subject.OwnedScopeIds);

        // The issued token carries this login's claims; everything else — secret, issuer, audience,
        // lifetime — comes from the configuration the validator also uses.
        var tokenConfiguration = configuration with { Claims = mapper.ToClaims(user) };

        return new AuthToken(
            jwtHandler.CreateToken(tokenConfiguration),
            DateTime.UtcNow.AddSeconds(configuration.ExpirationInSeconds));
    }
}
