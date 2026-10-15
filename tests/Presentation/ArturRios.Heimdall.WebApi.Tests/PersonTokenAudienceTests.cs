using System.IdentityModel.Tokens.Jwt;
using System.Text;
using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Heimdall.Command.Services;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.WebApi.Security;
using ArturRios.Jwt;
using ArturRios.Output;
using ArturRios.Util.Test.Attributes;
using Microsoft.IdentityModel.Tokens;

namespace ArturRios.Heimdall.WebApi.Tests;

// Unit tests for the audiences of the tokens Heimdall issues to a person (TH-06, TH-35). The
// challenge token and the subject-rights-only token are signed with the full token's keys under its
// issuer, so the audience is what tells a consumer they are not a sign-in: each issuer must stamp its
// own, and a consumer that pins the person audience must refuse both while it accepts a full token —
// asserted here with nothing but the standard handler, as a consumer would validate.
public class PersonTokenAudienceTests
{
    private const string Issuer = "heimdall-unit";
    private const string PersonAudience = "heimdall-unit";
    private const string Secret = "unit-tests-person-token-signing-secret-0123456789";

    private static readonly PersonTokenAudiences Audiences = new(PersonAudience);

    private static readonly IdentityUserMapper Mapper = new(Audiences);

    private static JwtConfiguration Configuration() =>
        new(3600, Issuer, PersonAudience, Secret, []) { Keys = [new JwtKey("current", Secret)] };

    private static JwtTwoFactorChallengeTokenIssuer ChallengeIssuer() =>
        new(Configuration(), Audiences, new JwtHandler(), Mapper);

    private static JwtAuthTokenIssuer AuthIssuer() =>
        new(Configuration(), Audiences, new JwtHandler(), Mapper, new UnreadPermissions());

    /// <summary>A System Admin, whose full token claims no scope and so reads no permissions.</summary>
    private static AuthTokenSubject SystemAdmin() =>
        new(Guid.NewGuid(), "Ada", (int)Roles.SystemAdmin, null, []);

    private static string AudienceOf(string token) =>
        Assert.Single(new JwtSecurityTokenHandler().ReadJwtToken(token).Audiences);

    /// <summary>What a consumer such as fortuna-api checks: signature, issuer, the person audience, lifetime.</summary>
    private static void ValidateAsAConsumer(string token) =>
        new JwtSecurityTokenHandler().ValidateToken(token, new TokenValidationParameters
        {
            ValidIssuer = Issuer,
            ValidateIssuer = true,
            ValidAudience = PersonAudience,
            ValidateAudience = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.ASCII.GetBytes(Secret)),
            ValidateIssuerSigningKey = true,
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            ValidateLifetime = true
        }, out _);

    [UnitFact]
    public async Task GivenAChallenge_WhenIssued_ThenItCarriesTheChallengeAudience()
    {
        // Given / When
        var challenge = await ChallengeIssuer().IssueAsync(Guid.NewGuid(), (int)Roles.SystemAdmin, Guid.NewGuid());

        // Then — the person audience with the fixed suffix a consumer can name
        Assert.Equal("heimdall-unit.2fa-challenge", AudienceOf(challenge.Token));
    }

    [UnitFact]
    public async Task GivenARestrictedSubject_WhenIssuingItsToken_ThenItCarriesTheSubjectRightsAudience()
    {
        // Given / When
        var token = await AuthIssuer().IssueSubjectRightsOnlyAsync(Guid.NewGuid(), (int)Roles.SystemAdmin);

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
        var challenge = await ChallengeIssuer().IssueAsync(Guid.NewGuid(), (int)Roles.SystemAdmin, Guid.NewGuid());

        // When / Then — refused for its audience alone, by a consumer that knows no mfaPending claim
        Assert.Throws<SecurityTokenInvalidAudienceException>(() => ValidateAsAConsumer(challenge.Token));
    }

    [UnitFact]
    public async Task GivenAConsumerPinningThePersonAudience_WhenValidatingASubjectRightsToken_ThenTheAudienceIsRefused()
    {
        // Given — what UC-11 hands a restricted identity (NFR-24)
        var token = await AuthIssuer().IssueSubjectRightsOnlyAsync(Guid.NewGuid(), (int)Roles.SystemAdmin);

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
    public async Task GivenAChallenge_WhenValidatedForVerification_ThenItIsAccepted()
    {
        // Given
        var personId = Guid.NewGuid();
        var challengeId = Guid.NewGuid();
        var issuer = ChallengeIssuer();
        var challenge = await issuer.IssueAsync(personId, (int)Roles.SystemAdmin, challengeId);

        // When
        var principal = await issuer.ValidateAsync(challenge.Token);

        // Then
        Assert.Equal(new TwoFactorChallengePrincipal(personId, challengeId), principal);
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

    /// <summary>
    ///     The permission reader a System Admin's full token never touches, since they act within no
    ///     scope. Every member throws, so a read would fail the test rather than pass unnoticed.
    /// </summary>
    private sealed class UnreadPermissions : IAsyncReadOnlyRepository<ScopePermission, long>
    {
        public IQueryable<ScopePermission> Query() => throw new InvalidOperationException("Not read.");

        public Task<DataOutput<IEnumerable<ScopePermission>>> GetAllAsync(CancellationToken ct = default) =>
            throw new InvalidOperationException("Not read.");

        public Task<DataOutput<ScopePermission?>> GetByIdAsync(long id, CancellationToken ct = default) =>
            throw new InvalidOperationException("Not read.");
    }
}
