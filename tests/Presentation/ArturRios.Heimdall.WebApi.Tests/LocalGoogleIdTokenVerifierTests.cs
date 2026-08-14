using System.Security.Claims;
using System.Text;
using ArturRios.Heimdall.WebApi.Security;
using ArturRios.Heimdall.WebApi.Tests.Support;
using ArturRios.Util.Test.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace ArturRios.Heimdall.WebApi.Tests;

// The other half of NFR-13's verification, and the half that decides what the functional suite is
// worth.
//
// UC-25's flows all sit behind token verification, so the suite reaches them by substituting
// LocalGoogleIdTokenVerifier — which checks a locally held signing secret instead of Google's
// published keys. That substitution is only sound if the substitute enforces the same four
// properties NFR-13 names. If it waved anything through, every UC-25 functional test would be
// exercising a code path no real deployment has, and the suite would be reporting on a system that
// does not exist.
//
// So these tests attack the verifier rather than use it: each one presents a token that is wrong in
// exactly one way and requires a refusal. What they do not cover is Google's own check — that stays
// verified by inspection, and GoogleIdTokenValidationSettingsTests pins the one input to it this
// application supplies.
public class LocalGoogleIdTokenVerifierTests
{
    private static readonly JsonWebTokenHandler Handler = new();

    private static LocalGoogleIdTokenVerifier Verifier() =>
        new(
            new GoogleSignInOptions { TestSigningSecret = PostgresFixture.GoogleTestSigningSecret },
            NullLogger<LocalGoogleIdTokenVerifier>.Instance);

    /// <summary>
    ///     Mints a token that is valid except for whatever the caller overrides, so each test below
    ///     differs from a good token in exactly one respect.
    /// </summary>
    private static string Token(
        string? issuer = null,
        string? audience = null,
        string? signingSecret = null,
        TimeSpan? expiresIn = null) =>
        Handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer ?? LocalGoogleIdTokenVerifier.TestIssuer,
            Audience = audience ?? LocalGoogleIdTokenVerifier.TestIssuer,
            Subject = new ClaimsIdentity([
                new Claim("sub", $"sub-{Guid.NewGuid():N}"),
                new Claim("email", $"caller-{Guid.NewGuid():N}@gmail.test")
            ]),
            Expires = DateTime.UtcNow.Add(expiresIn ?? TimeSpan.FromMinutes(10)),
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(
                    signingSecret ?? PostgresFixture.GoogleTestSigningSecret)),
                SecurityAlgorithms.HmacSha256)
        });

    [UnitFact]
    public async Task GivenAWellFormedToken_WhenVerified_ThenItsClaimsAreReturned()
    {
        // The control. Without it, a verifier that refused everything would pass every test below.
        var payload = await Verifier().VerifyAsync(Token());

        Assert.NotNull(payload);
        Assert.False(string.IsNullOrWhiteSpace(payload.Subject));
        Assert.False(string.IsNullOrWhiteSpace(payload.Email));
    }

    [UnitFact]
    public async Task GivenATokenSignedWithAnotherKey_WhenVerified_ThenItIsRefused()
    {
        // NFR-13, signature. The forgery an attacker actually attempts: correct claims, correct
        // issuer, correct audience, wrong key.
        Assert.Null(await Verifier().VerifyAsync(Token(signingSecret: "a-completely-different-secret-value")));
    }

    [UnitFact]
    public async Task GivenATokenFromAnotherIssuer_WhenVerified_ThenItIsRefused()
    {
        // NFR-13, issuer.
        Assert.Null(await Verifier().VerifyAsync(Token(issuer: "https://accounts.not-google.test")));
    }

    [UnitFact]
    public async Task GivenATokenForAnotherAudience_WhenVerified_ThenItIsRefused()
    {
        // NFR-13, audience — the clause that stops a token minted for a different application being
        // accepted by this one even though it was properly signed.
        Assert.Null(await Verifier().VerifyAsync(Token(audience: "some-other-application")));
    }

    [UnitFact]
    public async Task GivenAnExpiredToken_WhenVerified_ThenItIsRefused()
    {
        // NFR-13, expiry. The verifier runs with no clock skew allowance, so a minute past is past.
        Assert.Null(await Verifier().VerifyAsync(Token(expiresIn: TimeSpan.FromMinutes(-1))));
    }

    [UnitFact]
    public async Task GivenATokenMissingAnIdentifyingClaim_WhenVerified_ThenItIsRefused()
    {
        // Not one of NFR-13's four, but the same boundary: a token that passes every cryptographic
        // check and still cannot name an account is refused rather than turned into a Google User
        // with an empty identity.
        var withoutEmail = Handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = LocalGoogleIdTokenVerifier.TestIssuer,
            Audience = LocalGoogleIdTokenVerifier.TestIssuer,
            Subject = new ClaimsIdentity([new Claim("sub", "sub-only")]),
            Expires = DateTime.UtcNow.AddMinutes(10),
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(PostgresFixture.GoogleTestSigningSecret)),
                SecurityAlgorithms.HmacSha256)
        });

        Assert.Null(await Verifier().VerifyAsync(withoutEmail));
    }

    [UnitFact]
    public async Task GivenNoTokenAtAll_WhenVerified_ThenItIsRefused()
    {
        Assert.Null(await Verifier().VerifyAsync(string.Empty));
        Assert.Null(await Verifier().VerifyAsync("   "));
        Assert.Null(await Verifier().VerifyAsync("not-a-jwt"));
    }
}
