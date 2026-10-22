using ArturRios.Heimdall.WebApi.Security;
using ArturRios.Jwt;

namespace ArturRios.Heimdall.WebApi;

internal static class JwtConfigurationBuilder
{
    private const string TokenAudienceEnvironmentVariable = "HEIMDALL_AUTH_TOKEN_AUDIENCE";
    private const string TokenExpirationEnvironmentVariable = "HEIMDALL_AUTH_TOKEN_EXPIRATION_IN_SECONDS";
    private const string TokenIssuerEnvironmentVariable = "HEIMDALL_AUTH_TOKEN_ISSUER";
    private const double DefaultTokenExpirationInSeconds = 3600;

    /// <summary>
    ///     Reads the token settings from the environment — lifetime, issuer and audience — and hands
    ///     them to <paramref name="keys" />, which makes the configuration sign ES256 with the current
    ///     key and accept the current and any previous one (<see cref="TokenSigningKeys.ApplyTo" />).
    ///     It holds EC keys alone and no secret, so an HS256 token is refused whatever it is signed with.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <c>HEIMDALL_AUTH_SIGNING_KEY_PREVIOUS</c> is how the signing key is replaced without
    ///         signing everybody out (Threat Model TH-22). Its public half is published beside the
    ///         current key's and verifies the tokens it signed, while new tokens are signed with the
    ///         current key alone. Rotating is then: set the previous variable to the key in use, set the
    ///         current variable to a new one, restart; and once the longest token lifetime has passed —
    ///         a person token's or an application token's, whichever is longer — clear the previous
    ///         variable and restart again.
    ///     </para>
    ///     <para>
    ///         Every token carries a <c>kid</c>: the RFC 7638 thumbprint of the key that signed it
    ///         (<see cref="JwtKey.Id" />). A thumbprint is derived from the public half, which the key
    ///         set publishes anyway, so it discloses nothing, and it stays attached to its key wherever
    ///         the key goes: a token signed before a rotation names the key that becomes the previous
    ///         one, and is checked against that key alone.
    ///     </para>
    /// </remarks>
    internal static JwtConfiguration BuildJwtConfiguration(TokenSigningKeys keys)
    {
        var lifetime = TokenLifetimeSetting.Parse(
            Environment.GetEnvironmentVariable(TokenExpirationEnvironmentVariable),
            DefaultTokenExpirationInSeconds);

        ConfigurationWarnings.WarnOnFallback(TokenExpirationEnvironmentVariable, lifetime.RejectedValue, DefaultTokenExpirationInSeconds);

        return keys.ApplyTo(new JwtConfiguration(
            lifetime.Seconds,
            Environment.GetEnvironmentVariable(TokenIssuerEnvironmentVariable) ?? string.Empty,
            Environment.GetEnvironmentVariable(TokenAudienceEnvironmentVariable) ?? string.Empty,
            string.Empty,
            []));
    }
}
