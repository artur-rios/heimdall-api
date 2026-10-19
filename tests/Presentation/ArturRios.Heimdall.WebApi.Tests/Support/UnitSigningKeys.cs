using System.Security.Cryptography;
using System.Text;
using ArturRios.Jwt;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace ArturRios.Heimdall.WebApi.Tests.Support;

/// <summary>
///     Generates fresh ES256 signing keys for unit tests, together with the value
///     <c>HEIMDALL_AUTH_SIGNING_KEY</c> would carry for each: the base64 of its PKCS#8 PEM, on one line.
///     Also mints tokens at chosen instants, expired or backdated, under any key.
/// </summary>
/// <remarks>
///     Every key is generated here, in memory, and never read from disk, so no test depends on, or
///     can disclose, a key any environment signs with.
/// </remarks>
public static class UnitSigningKeys
{
    /// <summary>A fresh P-256 signing key and its environment value.</summary>
    public static (JwtKey Key, string EnvValue) Create()
    {
        var (ecdsa, value) = CreateEcdsa();

        using (ecdsa)
        {
            return (JwtKey.FromEcPrivateKeyPem(ecdsa.ExportPkcs8PrivateKeyPem()), value);
        }
    }

    /// <summary>
    ///     A fresh P-256 key as the platform holds it, for tests that need its coordinates, and its
    ///     environment value. The caller owns the <see cref="ECDsa" />.
    /// </summary>
    public static (ECDsa Ecdsa, string EnvValue) CreateEcdsa()
    {
        var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        return (ecdsa, EnvValueOf(ecdsa.ExportPkcs8PrivateKeyPem()));
    }

    /// <summary>The base64 (standard alphabet, one line) of <paramref name="text" />'s UTF-8 bytes.</summary>
    public static string EnvValueOf(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

    /// <summary>
    ///     A token signed with <paramref name="credentials" /> that carries exactly the instants given
    ///     and no others: <c>null</c> leaves that claim out.
    /// </summary>
    /// <remarks>
    ///     <see cref="JsonWebTokenHandler" /> rather than <see cref="JwtHandler" />, which stamps the
    ///     current time and cannot backdate a token. It is also the only way to make an expired one:
    ///     <see cref="JwtHandler" /> given a negative lifetime puts <c>exp</c> before <c>nbf</c>, which
    ///     IdentityModel refuses to mint (IDX12401).
    /// </remarks>
    public static string TokenAt(
        SigningCredentials credentials,
        IDictionary<string, object> claims,
        string issuer,
        string audience,
        DateTime? issuedAt,
        DateTime? notBefore,
        DateTime? expires) =>
        new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false }.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            Claims = claims,
            IssuedAt = issuedAt,
            NotBefore = notBefore,
            Expires = expires,
            SigningCredentials = credentials
        });

    /// <summary>
    ///     A token that expired a minute ago, issued and valid from twenty minutes ago: the one way
    ///     tests mint an expired token.
    /// </summary>
    public static string ExpiredToken(
        SigningCredentials credentials, IDictionary<string, object> claims, string issuer, string audience)
    {
        var now = DateTime.UtcNow;

        return TokenAt(
            credentials, claims, issuer, audience, now.AddMinutes(-20), now.AddMinutes(-20), now.AddMinutes(-1));
    }

    /// <summary>
    ///     HS256 credentials over <paramref name="secret" />'s ASCII bytes, as <c>ArturRios.Jwt</c> reads
    ///     a secret. A <paramref name="keyId" /> is written to the token's <c>kid</c> header.
    /// </summary>
    public static SigningCredentials Hs256(string secret, string? keyId = null) =>
        new(new SymmetricSecurityKey(Encoding.ASCII.GetBytes(secret)) { KeyId = keyId },
            SecurityAlgorithms.HmacSha256);

    /// <summary>ES256 credentials over <paramref name="key" />, whose thumbprint becomes the token's <c>kid</c>.</summary>
    public static SigningCredentials Es256(JwtKey key) => new(key.ToSecurityKey(), SecurityAlgorithms.EcdsaSha256);
}
