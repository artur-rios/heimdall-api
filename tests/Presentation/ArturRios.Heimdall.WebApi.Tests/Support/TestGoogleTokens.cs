using System.Security.Claims;
using System.Text;
using ArturRios.Heimdall.WebApi.Security;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace ArturRios.Heimdall.WebApi.Tests.Support;

/// <summary>
///     Mints the stand-in Google ID tokens UC-25's functional tests present, signed with the secret
///     <see cref="PostgresFixture" /> publishes so the host under test's
///     <see cref="LocalGoogleIdTokenVerifier" /> accepts them.
/// </summary>
/// <remarks>
///     The counterpart of <see cref="TestTokens" />, which mints the application's own tokens: this
///     one mints what a caller brings <em>in</em>, that one what the API hands <em>out</em>. Tokens
///     are properly signed rather than waved through, so the suite exercises the same verification
///     path a real deployment does — only the signing authority differs.
/// </remarks>
public static class TestGoogleTokens
{
    private static readonly JsonWebTokenHandler Handler = new();

    /// <summary>Builds a valid ID token carrying the claims a Google User is populated from.</summary>
    /// <param name="subject">Google's <c>sub</c>. Defaults to a fresh value, i.e. an unknown account.</param>
    /// <param name="email">The <c>email</c> claim.</param>
    /// <param name="emailVerified">The <c>email_verified</c> claim.</param>
    /// <param name="name">The <c>name</c> claim; omitted from the token when null.</param>
    /// <param name="pictureUrl">The <c>picture</c> claim; omitted from the token when null.</param>
    /// <param name="expiresIn">Lifetime, so a test can mint an expired token for AF-25a.</param>
    public static string For(
        string? subject = null,
        string? email = null,
        bool emailVerified = true,
        string? name = "Google Signer",
        string? pictureUrl = "https://lh3.googleusercontent.test/a/photo",
        TimeSpan? expiresIn = null)
    {
        var claims = new List<Claim>
        {
            new("sub", subject ?? $"google-sub-{Guid.NewGuid():N}"),
            new("email", email ?? $"google-{Guid.NewGuid():N}@gmail.test"),
            new("email_verified", emailVerified ? "true" : "false")
        };

        if (name is not null)
        {
            claims.Add(new Claim("name", name));
        }

        if (pictureUrl is not null)
        {
            claims.Add(new Claim("picture", pictureUrl));
        }

        return Handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = LocalGoogleIdTokenVerifier.TestIssuer,
            Audience = LocalGoogleIdTokenVerifier.TestIssuer,
            Subject = new ClaimsIdentity(claims),
            Expires = DateTime.UtcNow.Add(expiresIn ?? TimeSpan.FromMinutes(10)),
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(PostgresFixture.GoogleTestSigningSecret)),
                SecurityAlgorithms.HmacSha256)
        });
    }

    /// <summary>
    ///     A token signed with the wrong key — what an attacker forging one looks like from the
    ///     API's side, for AF-25a.
    /// </summary>
    public static string SignedWithWrongSecret() =>
        Handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = LocalGoogleIdTokenVerifier.TestIssuer,
            Audience = LocalGoogleIdTokenVerifier.TestIssuer,
            Subject = new ClaimsIdentity([
                new Claim("sub", $"google-sub-{Guid.NewGuid():N}"),
                new Claim("email", $"forged-{Guid.NewGuid():N}@gmail.test")
            ]),
            Expires = DateTime.UtcNow.AddMinutes(10),
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(
                    Encoding.UTF8.GetBytes("a-completely-different-signing-secret-value")),
                SecurityAlgorithms.HmacSha256)
        });
}
