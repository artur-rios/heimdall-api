using System.Text;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.WebApi.Security;
using ArturRios.Jwt;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.WebApi.Security.Authentication;
using ArturRios.Util.WebApi.Security.Configuration;
using ArturRios.Util.WebApi.Security.Enums;
using Microsoft.AspNetCore.Http;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace ArturRios.Heimdall.WebApi.Tests;

// Unit tests for TokenOrigin and PersonTokenValidator (Threat Model TB-5). The signing key is not
// Heimdall's alone, so a correct signature proves only that the key signed a token: each reader also
// requires Heimdall's issuer and exactly one of the audiences it takes. The bearer validator applies
// that after the library's signature and lifetime check, and answers a refusal as it answers a bad
// signature.
public class TokenOriginTests
{
    private const string Issuer = "heimdall-unit";
    private const string PersonAudience = "heimdall-unit";
    private const string Secret = "unit-tests-token-origin-signing-secret-0123456789";

    private static readonly PersonTokenAudiences Audiences = new(PersonAudience);

    private static readonly IdentityUserMapper Mapper = new(Audiences);

    private static JwtConfiguration Configuration(string issuer = Issuer) =>
        new(3600, issuer, PersonAudience, Secret, []) { Keys = [new JwtKey("current", Secret)] };

    private static string Mint(IdentityUser user, string issuer, string audience) =>
        new JwtHandler().CreateToken(new JwtConfiguration(3600, issuer, audience, Secret, Mapper.ToClaims(user)));

    private static string MintWithAudiences(string issuer, params string[] audiences) =>
        new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Claims = new Dictionary<string, object> { [JwtRegisteredClaimNames.Aud] = audiences },
            Expires = DateTime.UtcNow.AddMinutes(10),
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.ASCII.GetBytes(Secret)), SecurityAlgorithms.HmacSha256)
        });

    private static IdentityUser FullIdentity() => new(Guid.NewGuid(), (int)Roles.SystemAdmin);

    private static PersonTokenValidator Validator(JwtConfiguration configuration)
    {
        var options = new AuthenticationOptions { EnableJwt = true, JwtMode = JwtValidationMode.ClaimsOnly };
        var handler = new JwtHandler();

        return new PersonTokenValidator(
            new JwtTokenValidator(configuration, handler, Mapper, options), configuration, Audiences);
    }

    // TokenOrigin

    [UnitFact]
    public void GivenTheExpectedIssuerAndOneAcceptedAudience_WhenChecked_ThenItIsIssuedForIt()
    {
        // Given
        var token = Mint(FullIdentity(), Issuer, Audiences.SubjectRights);

        // When / Then
        Assert.True(TokenOrigin.IsIssuedFor(token, Issuer, Audiences.Person, Audiences.SubjectRights));
    }

    [UnitTheory]
    [InlineData("another-service", PersonAudience)]
    [InlineData(Issuer, "another-service")]
    [InlineData("", PersonAudience)]
    [InlineData("HEIMDALL-UNIT", PersonAudience)]
    [InlineData(Issuer, "HEIMDALL-UNIT")]
    [InlineData(Issuer, PersonAudience + "/")]
    public void GivenAnotherIssuerOrAudience_WhenChecked_ThenItIsNotIssuedForIt(string issuer, string audience)
    {
        // Given — exact and ordinal: no case folding, no trailing-slash leniency
        var token = Mint(FullIdentity(), issuer, audience);

        // When / Then
        Assert.False(TokenOrigin.IsIssuedFor(token, Issuer, PersonAudience));
    }

    [UnitFact]
    public void GivenAnAcceptedAudienceBesideAnother_WhenChecked_ThenItIsNotIssuedForIt()
    {
        // Given — Heimdall never issues a token with two audiences
        var token = MintWithAudiences(Issuer, PersonAudience, "another-service");

        // When / Then
        Assert.False(TokenOrigin.IsIssuedFor(token, Issuer, PersonAudience));
    }

    [UnitFact]
    public void GivenNoIssuerConfigured_WhenCheckingATokenWithoutOne_ThenItIsIssuedForIt()
    {
        // Given — how Heimdall issues when HEIMDALL_AUTH_TOKEN_ISSUER is empty: no iss claim at all
        var token = Mint(FullIdentity(), string.Empty, PersonAudience);
        Assert.False(new JsonWebToken(token).TryGetClaim(JwtRegisteredClaimNames.Iss, out _));

        // When / Then
        Assert.True(TokenOrigin.IsIssuedFor(token, string.Empty, PersonAudience));
        Assert.False(TokenOrigin.IsIssuedFor(
            Mint(FullIdentity(), Issuer, PersonAudience), string.Empty, PersonAudience));
    }

    [UnitTheory]
    [InlineData("")]
    [InlineData("not-a-token")]
    [InlineData("a.b.c")]
    public void GivenGarbage_WhenChecked_ThenItIsNotIssuedForAnything(string token)
    {
        // Given / When / Then
        Assert.False(TokenOrigin.IsIssuedFor(token, Issuer, PersonAudience));
    }

    // PersonTokenValidator

    [UnitTheory]
    [InlineData(PersonAudience)]
    [InlineData(PersonAudience + PersonTokenAudiences.SubjectRightsSuffix)]
    [InlineData(PersonAudience + PersonTokenAudiences.TwoFactorChallengeSuffix)]
    public async Task GivenATokenUnderEachPersonAudience_WhenValidated_ThenTheUserIsResolved(string audience)
    {
        // Given — each person token with the marks its audience calls for
        var user = new IdentityUser(Guid.NewGuid(), (int)Roles.SystemAdmin)
        {
            SubjectRightsOnly = audience == Audiences.SubjectRights,
            MfaPending = audience == Audiences.TwoFactorChallenge,
            ChallengeId = audience == Audiences.TwoFactorChallenge ? Guid.NewGuid() : null
        };
        var token = Mint(user, Issuer, audience);

        // When
        var result = await Validator(Configuration()).ValidateAsync(token, new DefaultHttpContext());

        // Then
        Assert.Equal(user.Id, result.User?.Id);
        Assert.Null(result.Error);
    }

    [UnitTheory]
    [InlineData("another-service", PersonAudience)]
    [InlineData(Issuer, "another-service")]
    [InlineData(Issuer, "heimdall-unit-applications")]
    public async Task GivenACorrectlySignedTokenFromElsewhere_WhenValidated_ThenItIsRefusedAsInvalid(
        string issuer, string audience)
    {
        // Given — the signature and lifetime the library checks are right
        var token = Mint(FullIdentity(), issuer, audience);

        // When
        var result = await Validator(Configuration()).ValidateAsync(token, new DefaultHttpContext());

        // Then — the answer a bad signature gets
        Assert.Null(result.User);
        Assert.Equal(PersonTokenValidator.InvalidToken, result.Error);
    }

    [UnitFact]
    public async Task GivenABadSignature_WhenValidated_ThenTheLibrarysRefusalPassesThrough()
    {
        // Given
        var token = new JwtHandler().CreateToken(new JwtConfiguration(
            3600, Issuer, PersonAudience, "some-other-signing-secret-0123456789abcdef",
            Mapper.ToClaims(FullIdentity())));

        // When
        var result = await Validator(Configuration()).ValidateAsync(token, new DefaultHttpContext());

        // Then
        Assert.Null(result.User);
        Assert.Equal(PersonTokenValidator.InvalidToken, result.Error);
    }

    [UnitTheory]
    [InlineData(SecurityAlgorithms.HmacSha384)]
    [InlineData(SecurityAlgorithms.HmacSha512)]
    public async Task GivenTheRightKeyUnderAnotherHmacAlgorithm_WhenValidated_ThenItIsRefused(string algorithm)
    {
        // Given — ArturRios.Jwt 1.3.0 accepts HS256 alone, the one algorithm Heimdall signs with. The
        // key is long enough for HS512, so only the algorithm is wrong.
        const string longSecret = "unit-tests-token-origin-signing-secret-long-enough-for-hs512-0123456789";
        var configuration = new JwtConfiguration(3600, Issuer, PersonAudience, longSecret, [])
        {
            Keys = [new JwtKey("current", longSecret)]
        };
        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = PersonAudience,
            Claims = Mapper.ToClaims(FullIdentity()).ToDictionary(claim => claim.Key, object (claim) => claim.Value),
            Expires = DateTime.UtcNow.AddMinutes(10),
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.ASCII.GetBytes(longSecret)), algorithm)
        });

        // When
        var result = await Validator(configuration).ValidateAsync(token, new DefaultHttpContext());

        // Then
        Assert.Null(result.User);
    }
}
