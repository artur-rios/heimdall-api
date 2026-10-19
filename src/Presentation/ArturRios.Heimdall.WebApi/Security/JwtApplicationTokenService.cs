using ArturRios.Heimdall.Command.Services;
using ArturRios.Heimdall.Shared.Security;
using ArturRios.Jwt;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace ArturRios.Heimdall.WebApi.Security;

/// <summary>
///     Issues and validates application tokens (UC-56, UC-57, FR-AP-13): ES256, signed with the same
///     key as person tokens, under the application audience.
/// </summary>
/// <remarks>
///     <para>
///         <b>Signing</b> goes through <see cref="JwtHandler" /> with the person configuration, so the
///         current key signs and a rotation (<c>HEIMDALL_AUTH_SIGNING_KEY_PREVIOUS</c>) covers
///         application tokens exactly as it covers person tokens. Only the audience, the lifetime and
///         the claims differ. No <c>roleId</c> is written: an application holds no Heimdall role.
///     </para>
///     <para>
///         <b>Validating</b> cannot go through <see cref="JwtHandler.IsTokenValidAsync(string, IEnumerable{JwtKey})" />,
///         which checks only the signature and lifetime. Here the issuer, the audience and the kind
///         are the point (TH-35), so the token is validated with every parameter set and no clock
///         skew, under the configured EC keys and ES256 alone. The standard issuer check runs only
///         when one is configured: <c>HEIMDALL_AUTH_TOKEN_ISSUER</c> defaults to empty, and an empty
///         expected issuer would refuse every token. <see cref="TokenOrigin" /> then holds the token to
///         exactly what Heimdall issues — the configured issuer, or none when none is configured, and
///         the application audience as its only audience — so a token signed under another issuer is
///         refused either way (TB-5).
///     </para>
///     <para>
///         <b>The overlap.</b> A token that pass refuses is validated once more, with the same
///         parameters, under HS256 alone and the keys <see cref="LegacyTokenValidator.SecurityKeysFor" />
///         offers: the legacy secrets, and only for a token Heimdall could have issued before this
///         process started within an application token's lifetime. No EC key is in that pass, and no
///         other key is tried, so neither algorithm's keys ever verify the other's tokens. An
///         acceptance by that pass is logged once the token has proved to be an application token.
///     </para>
/// </remarks>
public class JwtApplicationTokenService(
    JwtConfiguration configuration,
    JwtHandler jwtHandler,
    ApplicationTokenOptions options,
    LegacyTokenValidator legacy) : IApplicationTokenIssuer, IApplicationTokenValidator
{
    private static readonly JsonWebTokenHandler Reader = new();

    // The configured ES256 keys, the current and any previous one, as IdentityModel holds them.
    private readonly IReadOnlyList<SecurityKey> _keys = [.. configuration.Keys.Select(key => key.ToSecurityKey())];

    public Task<AuthToken> IssueAsync(ApplicationTokenSubject subject)
    {
        var claims = new Dictionary<string, string>
        {
            [ApplicationTokenClaims.TokenKind] = ApplicationTokenClaims.ApplicationKind,
            [ApplicationTokenClaims.Id] = subject.ApplicationId.ToString(),
            [ApplicationTokenClaims.ScopeId] = subject.ScopeId.ToString(),
            [ApplicationTokenClaims.CredentialId] = subject.CredentialId.ToString(),
            [ApplicationTokenClaims.Name] = subject.Name
        };

        var tokenConfiguration = configuration with
        {
            Audience = options.Audience,
            ExpirationInSeconds = options.Lifetime.TotalSeconds,
            Claims = claims
        };

        return Task.FromResult(new AuthToken(
            jwtHandler.CreateToken(tokenConfiguration),
            DateTime.UtcNow.Add(options.Lifetime)));
    }

    public async Task<ApplicationTokenPrincipal?> ValidateAsync(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var result = await ValidateUnderAsync(token, _keys, SecurityAlgorithms.EcdsaSha256);
        var byLegacy = false;

        if (!result.IsValid && legacy.Enabled)
        {
            var legacyKeys = legacy.SecurityKeysFor(token, options.Lifetime);

            if (legacyKeys.Count > 0)
            {
                result = await ValidateUnderAsync(token, legacyKeys, SecurityAlgorithms.HmacSha256);
                byLegacy = result.IsValid;
            }
        }

        if (!result.IsValid ||
            !TokenOrigin.IsIssuedFor(token, configuration.Issuer, options.Audience) ||
            Claim(result, ApplicationTokenClaims.TokenKind) != ApplicationTokenClaims.ApplicationKind ||
            !Guid.TryParse(Claim(result, ApplicationTokenClaims.Id), out var applicationId) ||
            !Guid.TryParse(Claim(result, ApplicationTokenClaims.ScopeId), out var scopeId) ||
            !Guid.TryParse(Claim(result, ApplicationTokenClaims.CredentialId), out var credentialId))
        {
            return null;
        }

        if (byLegacy)
        {
            legacy.Accepted(LegacyTokenValidator.ApplicationReader);
        }

        return new ApplicationTokenPrincipal(applicationId, scopeId, credentialId, result.SecurityToken.ValidTo);
    }

    /// <summary>
    ///     Validates <paramref name="token" /> under <paramref name="keys" /> and <paramref name="algorithm" />
    ///     alone, with the issuer, the application audience, a required lifetime and no clock skew.
    /// </summary>
    private Task<TokenValidationResult> ValidateUnderAsync(
        string token, IEnumerable<SecurityKey> keys, string algorithm) =>
        Reader.ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidIssuer = configuration.Issuer,
            ValidateIssuer = !string.IsNullOrWhiteSpace(configuration.Issuer),
            ValidAudience = options.Audience,
            ValidateAudience = true,
            IssuerSigningKeys = keys,
            ValidateIssuerSigningKey = true,
            ValidAlgorithms = [algorithm],
            ValidateLifetime = true,
            RequireExpirationTime = true,
            ClockSkew = TimeSpan.Zero
        });

    private static string? Claim(TokenValidationResult result, string name) =>
        result.Claims.TryGetValue(name, out var value) ? value?.ToString() : null;
}
