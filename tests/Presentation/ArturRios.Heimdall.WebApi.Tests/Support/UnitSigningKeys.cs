using System.Security.Cryptography;
using System.Text;
using ArturRios.Jwt;

namespace ArturRios.Heimdall.WebApi.Tests.Support;

/// <summary>
///     Generates fresh ES256 signing keys for unit tests, together with the value
///     <c>HEIMDALL_AUTH_SIGNING_KEY</c> would carry for each: the base64 of its PKCS#8 PEM, on one line.
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
}
