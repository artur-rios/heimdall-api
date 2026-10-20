using System.IdentityModel.Tokens.Jwt;
using System.Text;
using ArturRios.Heimdall.Command.Services;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.WebApi.Security;
using ArturRios.Heimdall.WebApi.Tests.Support;
using ArturRios.Jwt;
using ArturRios.Util.Test.Attributes;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace ArturRios.Heimdall.WebApi.Tests;

// Unit tests for the audiences of the tokens Heimdall issues to a person (TH-06, TH-35). The
// challenge token and the subject-rights-only token are signed with the full token's key under its
// issuer, so the audience is what tells a consumer they are not a sign-in: each issuer must stamp its
// own, and a consumer that pins the person audience must refuse both while it accepts a full token —
// asserted here with nothing but the standard handler, as a consumer would validate.
public class PersonTokenAudienceTests
{
    private const string Issuer = "heimdall-unit";
    private const string PersonAudience = "heimdall-unit";

    private static readonly PersonTokenAudiences Audiences = new(PersonAudience);

    private static readonly IdentityUserMapper Mapper = new(Audiences);

    // Heimdall's issuers and readers over one ES256 key, as start-up wires them.
    private readonly UnitTokenReaders _readers = new(issuer: Issuer, personAudience: PersonAudience);

    private JwtConfiguration Configuration() => _readers.Configuration;

    private JwtTwoFactorChallengeTokenIssuer ChallengeIssuer() => _readers.Challenge;

    private JwtAuthTokenIssuer AuthIssuer() => _readers.AuthIssuer;

    /// <summary>A System Admin, whose full token claims no scope and so reads no permissions.</summary>
    private static AuthTokenSubject SystemAdmin() =>
        new(Guid.NewGuid(), "Ada", (int)Roles.SystemAdmin, null, [], Guid.NewGuid());

    private static string AudienceOf(string token) =>
        Assert.Single(new JwtSecurityTokenHandler().ReadJwtToken(token).Audiences);

    /// <summary>
    ///     What a consumer such as fortuna-api checks: the signature under the published public key,
    ///     ES256 alone, the issuer, the person audience and the lifetime.
    /// </summary>
    private TokenValidationParameters ConsumerParameters() =>
        new()
        {
            ValidIssuer = Issuer,
            ValidateIssuer = true,
            ValidAudience = PersonAudience,
            ValidateAudience = true,
            IssuerSigningKey = _readers.Keys.Current.ToPublicKey().ToSecurityKey(),
            ValidateIssuerSigningKey = true,
            ValidAlgorithms = [SecurityAlgorithms.EcdsaSha256],
            ValidateLifetime = true
        };

    /// <summary>A consumer validating with <see cref="JwtSecurityTokenHandler" />.</summary>
    private void ValidateAsAConsumer(string token) =>
        new JwtSecurityTokenHandler().ValidateToken(token, ConsumerParameters(), out _);

    /// <summary>
    ///     A consumer validating with <see cref="JsonWebTokenHandler" />, which ASP.NET Core's
    ///     <c>AddJwtBearer</c> uses by default — fortuna-api's runtime path.
    /// </summary>
    private Task<TokenValidationResult> ValidateAsAJwtBearerConsumerAsync(string token) =>
        new JsonWebTokenHandler().ValidateTokenAsync(token, ConsumerParameters());

    [UnitFact]
    public async Task GivenAChallenge_WhenIssued_ThenItCarriesTheChallengeAudience()
    {
        // Given / When
        var challenge = await ChallengeIssuer().IssueAsync(Guid.NewGuid(), (int)Roles.SystemAdmin, Guid.NewGuid(), Guid.NewGuid());

        // Then — the person audience with the fixed suffix a consumer can name
        Assert.Equal("heimdall-unit.2fa-challenge", AudienceOf(challenge.Token));
    }

    [UnitFact]
    public async Task GivenARestrictedSubject_WhenIssuingItsToken_ThenItCarriesTheSubjectRightsAudience()
    {
        // Given / When
        var token = await AuthIssuer().IssueSubjectRightsOnlyAsync(Guid.NewGuid(), (int)Roles.SystemAdmin, Guid.NewGuid());

        // Then
        Assert.Equal("heimdall-unit.subject-rights", AudienceOf(token.Token));
    }

    [UnitFact]
    public async Task GivenASubject_WhenIssuingAFullToken_ThenItCarriesThePersonAudienceUnchanged()
    {
        // Given / When
        var token = await AuthIssuer().IssueAsync(SystemAdmin());

        // Then
        Assert.Equal(PersonAudience, AudienceOf(token.Token));
    }

    [UnitFact]
    public async Task GivenAConsumerPinningThePersonAudience_WhenValidatingAChallenge_ThenTheAudienceIsRefused()
    {
        // Given — what UC-11 hands anyone holding the password of a 2FA-gated account
        var challenge = await ChallengeIssuer().IssueAsync(Guid.NewGuid(), (int)Roles.SystemAdmin, Guid.NewGuid(), Guid.NewGuid());

        // When / Then — refused for its audience alone, by a consumer that knows no mfaPending claim
        Assert.Throws<SecurityTokenInvalidAudienceException>(() => ValidateAsAConsumer(challenge.Token));
    }

    [UnitFact]
    public async Task GivenAConsumerPinningThePersonAudience_WhenValidatingASubjectRightsToken_ThenTheAudienceIsRefused()
    {
        // Given — what UC-11 hands a restricted identity (NFR-24)
        var token = await AuthIssuer().IssueSubjectRightsOnlyAsync(Guid.NewGuid(), (int)Roles.SystemAdmin, Guid.NewGuid());

        // When / Then
        Assert.Throws<SecurityTokenInvalidAudienceException>(() => ValidateAsAConsumer(token.Token));
    }

    [UnitFact]
    public async Task GivenAConsumerPinningThePersonAudience_WhenValidatingAFullToken_ThenItIsAccepted()
    {
        // Given
        var token = await AuthIssuer().IssueAsync(SystemAdmin());

        // When
        var exception = Record.Exception(() => ValidateAsAConsumer(token.Token));

        // Then
        Assert.Null(exception);
    }

    [UnitFact]
    public async Task GivenAJwtBearerConsumerPinningThePersonAudience_WhenValidatingChallengeAndRightsTokens_ThenBothAudiencesAreRefused()
    {
        // Given — both tokens a password or a restricted identity obtains
        var challenge = await ChallengeIssuer().IssueAsync(Guid.NewGuid(), (int)Roles.SystemAdmin, Guid.NewGuid(), Guid.NewGuid());
        var rightsOnly = await AuthIssuer().IssueSubjectRightsOnlyAsync(Guid.NewGuid(), (int)Roles.SystemAdmin, Guid.NewGuid());

        // When
        var results = new[]
        {
            await ValidateAsAJwtBearerConsumerAsync(challenge.Token),
            await ValidateAsAJwtBearerConsumerAsync(rightsOnly.Token)
        };

        // Then — refused for the audience, as the other handler refuses them
        Assert.All(results, result =>
        {
            Assert.False(result.IsValid);
            Assert.IsType<SecurityTokenInvalidAudienceException>(result.Exception);
        });
    }

    [UnitFact]
    public async Task GivenAJwtBearerConsumerPinningThePersonAudience_WhenValidatingAFullToken_ThenItIsAccepted()
    {
        // Given
        var token = await AuthIssuer().IssueAsync(SystemAdmin());

        // When
        var result = await ValidateAsAJwtBearerConsumerAsync(token.Token);

        // Then
        Assert.True(result.IsValid, result.Exception?.Message);
    }

    [UnitFact]
    public async Task GivenAChallenge_WhenValidatedForVerification_ThenItIsAccepted()
    {
        // Given
        var personId = Guid.NewGuid();
        var challengeId = Guid.NewGuid();
        var securityStamp = Guid.NewGuid();
        var issuer = ChallengeIssuer();
        var challenge = await issuer.IssueAsync(personId, (int)Roles.SystemAdmin, challengeId, securityStamp);

        // When
        var principal = await issuer.ValidateAsync(challenge.Token);

        // Then — the stamp comes back too, for UC-38 to hold against the person's (FR-AU-11)
        Assert.Equal(new TwoFactorChallengePrincipal(personId, challengeId, securityStamp), principal);
    }

    [UnitFact]
    public async Task GivenAPendingMarkUnderThePersonAudience_WhenValidatedForVerification_ThenItIsRefused()
    {
        // Given — a correctly signed token claiming mfaPending and a challenge, but under the full
        // token's audience: not what the challenge issuer writes, so not a challenge
        var configuration = Configuration() with
        {
            Claims = Mapper.ToClaims(new IdentityUser(Guid.NewGuid(), (int)Roles.SystemAdmin)
            {
                MfaPending = true, ChallengeId = Guid.NewGuid()
            })
        };
        var crafted = new JwtHandler().CreateToken(configuration);

        // When
        var principal = await ChallengeIssuer().ValidateAsync(crafted);

        // Then
        Assert.Null(principal);
    }

    [UnitTheory]
    [InlineData("another-service", "heimdall-unit.2fa-challenge")]
    [InlineData(Issuer, "another-service")]
    public async Task GivenAChallengeFromAnotherIssuerOrAudience_WhenValidatedForVerification_ThenItIsRefused(
        string issuer, string audience)
    {
        // Given — a correctly signed challenge-shaped token that Heimdall did not issue for itself (TB-5)
        var configuration = Configuration() with
        {
            Issuer = issuer,
            Audience = audience,
            Claims = Mapper.ToClaims(new IdentityUser(Guid.NewGuid(), (int)Roles.SystemAdmin)
            {
                MfaPending = true, ChallengeId = Guid.NewGuid()
            })
        };
        var crafted = new JwtHandler().CreateToken(configuration);

        // When
        var principal = await ChallengeIssuer().ValidateAsync(crafted);

        // Then
        Assert.Null(principal);
    }

    [UnitTheory]
    [InlineData(SecurityAlgorithms.HmacSha256)]
    [InlineData(SecurityAlgorithms.HmacSha384)]
    [InlineData(SecurityAlgorithms.HmacSha512)]
    public async Task GivenAChallengeUnderAnHmacAlgorithm_WhenValidatedForVerification_ThenItIsRefused(string algorithm)
    {
        // Given — a challenge as a 2.x process issued it, signed with the shared secret 4.0.0 retired
        // (TH-43) and issued before this process started, under each algorithm from one descriptor, so
        // only the algorithm differs. The secret is long enough for HS512.
        const string longSecret = "unit-tests-person-token-signing-secret-long-enough-for-hs512-0123456789";
        var readers = new UnitTokenReaders(issuer: Issuer, personAudience: PersonAudience);
        var token = readers.TokenFor(
            UnitTokenReaders.ChallengeReader,
            new SigningCredentials(new SymmetricSecurityKey(Encoding.ASCII.GetBytes(longSecret)), algorithm));
        Assert.Equal(algorithm, new JsonWebToken(token).Alg);

        // When
        var principal = await readers.Challenge.ValidateAsync(token);

        // Then
        Assert.Null(principal);
    }
}
