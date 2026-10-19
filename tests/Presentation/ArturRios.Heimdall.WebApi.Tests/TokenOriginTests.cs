using System.Text;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.WebApi.Security;
using ArturRios.Heimdall.WebApi.Tests.Support;
using ArturRios.Jwt;
using ArturRios.Util.Test.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace ArturRios.Heimdall.WebApi.Tests;

// Unit tests for TokenOrigin and PersonTokenValidator (Threat Model TB-5). Heimdall signs several
// kinds of token with one key, and during the overlap its readers accept HS256 tokens signed with a
// secret that is not Heimdall's alone, so a correct signature proves only that a key signed a token:
// each reader also requires Heimdall's issuer and exactly one of the audiences it takes. The bearer
// validator applies that after the signature and lifetime check, and answers a refusal as it answers
// a bad signature.
public class TokenOriginTests
{
    private const string Issuer = "heimdall-unit";
    private const string PersonAudience = "heimdall-unit";

    private static readonly PersonTokenAudiences Audiences = new(PersonAudience);

    private static readonly IdentityUserMapper Mapper = new(Audiences);

    // The ES256 key Heimdall signs with here; every token below that is meant to be signed correctly is
    // signed with it.
    private static readonly TokenSigningKeys Keys = UnitTokenReaders.NewKeys();

    private static string Mint(IdentityUser user, string issuer, string audience) =>
        new JwtHandler().CreateToken(
            Keys.ApplyTo(new JwtConfiguration(3600, issuer, audience, string.Empty, Mapper.ToClaims(user))));

    private static string MintWithAudiences(string issuer, params string[] audiences) =>
        new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Claims = new Dictionary<string, object> { [JwtRegisteredClaimNames.Aud] = audiences },
            Expires = DateTime.UtcNow.AddMinutes(10),
            SigningCredentials = UnitSigningKeys.Es256(Keys.Current)
        });

    private static IdentityUser FullIdentity() => new(Guid.NewGuid(), (int)Roles.SystemAdmin);

    // The bearer reader over that key, as start-up wires it; no legacy secret.
    private static PersonTokenValidator Validator() => new UnitTokenReaders([], Keys, Issuer, PersonAudience).Bearer;

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
        var result = await Validator().ValidateAsync(token, new DefaultHttpContext());

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
        var result = await Validator().ValidateAsync(token, new DefaultHttpContext());

        // Then — the answer a bad signature gets
        Assert.Null(result.User);
        Assert.Equal(PersonTokenValidator.InvalidToken, result.Error);
    }

    [UnitFact]
    public async Task GivenABadSignature_WhenValidated_ThenTheLibrarysRefusalPassesThrough()
    {
        // Given
        var token = new JwtHandler().CreateToken(UnitTokenReaders.NewKeys().ApplyTo(
            new JwtConfiguration(3600, Issuer, PersonAudience, string.Empty, Mapper.ToClaims(FullIdentity()))));

        // When
        var result = await Validator().ValidateAsync(token, new DefaultHttpContext());

        // Then
        Assert.Null(result.User);
        Assert.Equal(PersonTokenValidator.InvalidToken, result.Error);
    }

    [UnitTheory]
    [InlineData(SecurityAlgorithms.HmacSha256, true)]
    [InlineData(SecurityAlgorithms.HmacSha384, false)]
    [InlineData(SecurityAlgorithms.HmacSha512, false)]
    public async Task GivenTheRightKeyUnderAnHmacAlgorithm_WhenValidated_ThenOnlyHs256IsAccepted(
        string algorithm, bool accepted)
    {
        // Given — during the overlap, a legacy token as a 2.x process issued it: the right legacy
        // secret, issued before this process started. HS256 was the one algorithm Heimdall signed
        // with, so it is the one the legacy path accepts. One descriptor for every case, with a key
        // long enough for HS512, so only the algorithm differs.
        const string longSecret = "unit-tests-token-origin-signing-secret-long-enough-for-hs512-0123456789";
        var readers = new UnitTokenReaders(TokenSigningSecrets.Parse(longSecret, null), Keys, Issuer, PersonAudience);
        var token = readers.TokenFor(
            LegacyTokenValidator.BearerReader,
            new SigningCredentials(new SymmetricSecurityKey(Encoding.ASCII.GetBytes(longSecret)), algorithm));
        Assert.Equal(algorithm, new JsonWebToken(token).Alg);

        // When
        var result = await readers.Bearer.ValidateAsync(token, new DefaultHttpContext());

        // Then
        if (accepted)
        {
            Assert.Equal(readers.Person.Id, result.User?.Id);
            Assert.Null(result.Error);
        }
        else
        {
            Assert.Null(result.User);
            Assert.Equal(PersonTokenValidator.InvalidToken, result.Error);
        }
    }

    [UnitTheory]
    [InlineData("")]
    [InlineData(PersonTokenAudiences.TwoFactorChallengeSuffix)]
    public async Task GivenAnEmptyPersonAudience_WhenValidatingATokenIssuedUnderIt_ThenTheUserIsResolved(string audience)
    {
        // Given — HEIMDALL_AUTH_TOKEN_AUDIENCE left empty, its documented default: a full token then
        // carries no aud claim, and a challenge carries the bare suffix
        var readers = new UnitTokenReaders([], Keys, Issuer, string.Empty);
        var audiences = readers.Audiences;
        var challenge = audience == audiences.TwoFactorChallenge;
        var user = new IdentityUser(Guid.NewGuid(), (int)Roles.SystemAdmin)
        {
            MfaPending = challenge,
            ChallengeId = challenge ? Guid.NewGuid() : null
        };
        var token = new JwtHandler().CreateToken(
            readers.Configuration with { Audience = audience, Claims = readers.Mapper.ToClaims(user) });
        Assert.Equal(challenge, new JsonWebToken(token).TryGetClaim(JwtRegisteredClaimNames.Aud, out _));

        var validator = readers.Bearer;

        // When
        var result = await validator.ValidateAsync(token, new DefaultHttpContext());

        // Then
        Assert.Equal(user.Id, result.User?.Id);
        Assert.Null(result.Error);
    }
}
