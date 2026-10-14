using System.Text;
using ArturRios.Heimdall.Command.Services;
using ArturRios.Heimdall.Shared.Security;
using ArturRios.Heimdall.WebApi.Security;
using ArturRios.Jwt;
using ArturRios.Util.Test.Attributes;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace ArturRios.Heimdall.WebApi.Tests;

// Unit tests for JwtApplicationTokenService (UC-56, UC-57, TH-34, TH-35): the token it issues carries
// the application claims and no role, is accepted by its own validator under the current or the
// previous key, and is refused for every other audience, kind, signature, algorithm or lifetime,
// including none at all. Negative cases
// mint their tokens directly, so each varies exactly one thing.
public class JwtApplicationTokenServiceTests
{
    private const string Issuer = "heimdall-unit";
    private const string PersonAudience = "heimdall-unit";
    private const string ApplicationAudience = "heimdall-unit-applications";
    // Longer than 512 bits, so an HS512 token signed with it is valid in every respect but its
    // algorithm: IdentityModel refuses a shorter key for HS512 outright, which would hide whether
    // the validator restricts the algorithm at all.
    private const string CurrentSecret =
        "unit-tests-current-signing-secret-0123456789-long-enough-to-sign-hs512-too";
    private const string PreviousSecret = "unit-tests-previous-signing-secret-0123456789";

    private static readonly ApplicationTokenSubject Subject =
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Billing Service");

    private static JwtConfiguration Configuration(string issuer = Issuer) =>
        new(3600, issuer, PersonAudience, CurrentSecret, [])
        {
            Keys = [new JwtKey("current", CurrentSecret), new JwtKey("previous", PreviousSecret)]
        };

    private static JwtApplicationTokenService Service(string issuer = Issuer) =>
        new(Configuration(issuer), new JwtHandler(),
            new ApplicationTokenOptions(ApplicationAudience, TimeSpan.FromMinutes(10)));

    private static Dictionary<string, object> ApplicationClaims() => new()
    {
        [ApplicationTokenClaims.TokenKind] = ApplicationTokenClaims.ApplicationKind,
        [ApplicationTokenClaims.Id] = Subject.ApplicationId.ToString(),
        [ApplicationTokenClaims.ScopeId] = Subject.ScopeId.ToString(),
        [ApplicationTokenClaims.CredentialId] = Subject.CredentialId.ToString(),
        [ApplicationTokenClaims.Name] = Subject.Name
    };

    private static string Mint(
        IDictionary<string, object> claims,
        string audience = ApplicationAudience,
        string secret = CurrentSecret,
        DateTime? expires = null,
        string issuer = Issuer,
        string algorithm = SecurityAlgorithms.HmacSha256,
        bool withLifetime = true)
    {
        var expiry = expires ?? DateTime.UtcNow.AddMinutes(10);

        // Without a lifetime the handler must not fill in its default one, so the token carries no exp.
        return new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = withLifetime }.CreateToken(
            new SecurityTokenDescriptor
            {
                Issuer = issuer,
                Audience = audience,
                Claims = claims,
                NotBefore = withLifetime ? expiry.AddMinutes(-20) : null,
                IssuedAt = withLifetime ? expiry.AddMinutes(-20) : null,
                Expires = withLifetime ? expiry : null,
                SigningCredentials = new SigningCredentials(
                    new SymmetricSecurityKey(Encoding.ASCII.GetBytes(secret)), algorithm)
            });
    }

    [UnitFact]
    public async Task GivenASubject_WhenIssuing_ThenTheTokenCarriesTheApplicationClaimsAndNoRole()
    {
        // Given
        var service = Service();

        // When
        var issued = await service.IssueAsync(Subject);

        // Then
        var token = new JsonWebToken(issued.Token);
        Assert.Equal(ApplicationTokenClaims.ApplicationKind, token.GetClaim(ApplicationTokenClaims.TokenKind).Value);
        Assert.Equal(Subject.ApplicationId.ToString(), token.GetClaim(ApplicationTokenClaims.Id).Value);
        Assert.Equal(Subject.ScopeId.ToString(), token.GetClaim(ApplicationTokenClaims.ScopeId).Value);
        Assert.Equal(Subject.CredentialId.ToString(), token.GetClaim(ApplicationTokenClaims.CredentialId).Value);
        Assert.Equal(Subject.Name, token.GetClaim(ApplicationTokenClaims.Name).Value);
        Assert.Equal([ApplicationAudience], token.Audiences);
        Assert.False(token.TryGetClaim("roleId", out _));
        Assert.InRange(issued.ExpiresAt, DateTime.UtcNow.AddMinutes(9), DateTime.UtcNow.AddMinutes(11));
    }

    [UnitFact]
    public async Task GivenAnIssuedToken_WhenValidated_ThenItsPrincipalIsReturned()
    {
        // Given
        var service = Service();
        var issued = await service.IssueAsync(Subject);

        // When
        var principal = await service.ValidateAsync(issued.Token);

        // Then
        Assert.NotNull(principal);
        Assert.Equal(Subject.ApplicationId, principal.ApplicationId);
        Assert.Equal(Subject.ScopeId, principal.ScopeId);
        Assert.Equal(Subject.CredentialId, principal.CredentialId);
    }

    [UnitFact]
    public async Task GivenNoIssuerConfigured_WhenRoundTripping_ThenTheTokenIsAccepted()
    {
        // Given — HEIMDALL_AUTH_TOKEN_ISSUER defaults to empty, and an empty ValidIssuer refuses all
        var service = Service(issuer: string.Empty);
        var issued = await service.IssueAsync(Subject);

        // When
        var principal = await service.ValidateAsync(issued.Token);

        // Then
        Assert.NotNull(principal);
    }

    [UnitFact]
    public async Task GivenATokenSignedWithThePreviousKey_WhenValidated_ThenItIsAccepted()
    {
        // Given — signed before a rotation (TH-22)
        var token = Mint(ApplicationClaims(), secret: PreviousSecret);

        // When
        var principal = await Service().ValidateAsync(token);

        // Then
        Assert.NotNull(principal);
    }

    [UnitFact]
    public async Task GivenATokenSignedWithAnUnknownKey_WhenValidated_ThenItIsRefused()
    {
        // Given
        var token = Mint(ApplicationClaims(), secret: "some-other-signing-secret-0123456789abcdef");

        // When
        var principal = await Service().ValidateAsync(token);

        // Then
        Assert.Null(principal);
    }

    [UnitFact]
    public async Task GivenThePersonAudience_WhenValidated_ThenItIsRefused()
    {
        // Given — every application claim, but minted for person consumers
        var token = Mint(ApplicationClaims(), audience: PersonAudience);

        // When
        var principal = await Service().ValidateAsync(token);

        // Then
        Assert.Null(principal);
    }

    [UnitFact]
    public async Task GivenAnotherIssuer_WhenValidated_ThenItIsRefused()
    {
        // Given
        var token = Mint(ApplicationClaims(), issuer: "someone-else");

        // When
        var principal = await Service().ValidateAsync(token);

        // Then
        Assert.Null(principal);
    }

    [UnitFact]
    public async Task GivenNoApplicationKind_WhenValidated_ThenItIsRefused()
    {
        // Given — right audience and key, but not an application token
        var claims = ApplicationClaims();
        claims.Remove(ApplicationTokenClaims.TokenKind);
        var token = Mint(claims);

        // When
        var principal = await Service().ValidateAsync(token);

        // Then
        Assert.Null(principal);
    }

    [UnitFact]
    public async Task GivenAnExpiredToken_WhenValidated_ThenItIsRefusedWithoutClockSkew()
    {
        // Given — expired five seconds ago, well inside the default five-minute skew
        var token = Mint(ApplicationClaims(), expires: DateTime.UtcNow.AddSeconds(-5));

        // When
        var principal = await Service().ValidateAsync(token);

        // Then
        Assert.Null(principal);
    }

    [UnitFact]
    public async Task GivenATokenSignedHs512WithTheCurrentKey_WhenValidated_ThenItIsRefused()
    {
        // Given — the right key, but only HS256 is ever issued, so only HS256 is accepted
        var token = Mint(ApplicationClaims(), algorithm: SecurityAlgorithms.HmacSha512);

        // When
        var principal = await Service().ValidateAsync(token);

        // Then
        Assert.Null(principal);
    }

    [UnitFact]
    public async Task GivenATokenWithoutAnExpiry_WhenValidated_ThenItIsRefused()
    {
        // Given — every application claim, but no exp: it would otherwise be valid for ever
        var token = Mint(ApplicationClaims(), withLifetime: false);
        Assert.False(new JsonWebToken(token).TryGetClaim(JwtRegisteredClaimNames.Exp, out _));

        // When
        var principal = await Service().ValidateAsync(token);

        // Then
        Assert.Null(principal);
    }

    [UnitTheory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-token")]
    [InlineData("a.b.c")]
    public async Task GivenGarbage_WhenValidated_ThenItIsRefused(string? token)
    {
        // Given / When
        var principal = await Service().ValidateAsync(token);

        // Then
        Assert.Null(principal);
    }

    [UnitFact]
    public async Task GivenAnUnreadableId_WhenValidated_ThenItIsRefused()
    {
        // Given
        var claims = ApplicationClaims();
        claims[ApplicationTokenClaims.CredentialId] = "not-a-guid";
        var token = Mint(claims);

        // When
        var principal = await Service().ValidateAsync(token);

        // Then
        Assert.Null(principal);
    }
}
