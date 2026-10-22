using System.Net;
using System.Security.Cryptography;
using System.Text;
using ArturRios.Configuration.Enums;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Query.Output;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Heimdall.Shared.Security;
using ArturRios.Heimdall.WebApi.Security;
using ArturRios.Heimdall.WebApi.Tests.Support;
using ArturRios.Jwt;
using ArturRios.Output;
using ArturRios.Util.Http;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Functional;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using static ArturRios.Heimdall.WebApi.Tests.Support.ApplicationSeeds;

namespace ArturRios.Heimdall.WebApi.Tests;

// Functional tests for the issuer and audience check every reader of a Heimdall token makes (Threat
// Model TB-5). Each token below is signed with the host's real key, so the signature and lifetime are
// right and only the issuer or the audience is not: the bearer path, the subject-rights endpoints,
// the challenge reader and the application-token check each refuse a token Heimdall did not issue,
// or did not issue for them, and each still accepts the one it did.
[Collection(nameof(FunctionalCollection))]
public class TokenIssuerAndAudienceTests(PostgresFixture db) : WebApiTest<Program>(EnvironmentType.Local)
{
    private const string Password = Seeds.Password;
    private const string ForeignIssuer = "another-service";
    private const string ForeignAudience = "another-service";

    private static string UniqueEmail(string prefix) => $"{prefix}-{Guid.NewGuid():N}@test.local";

    /// <summary>
    ///     An active configuration with an outstanding challenge and one recovery code to redeem it with.
    /// </summary>
    private async Task<string> SeedTwoFactorWithRecoveryCodeAsync(Person person)
    {
        await using var context = db.CreateContext();

        var twoFactorAuth = new TwoFactorAuth
        {
            PersonId = person.Id, IsActive = true, AppEnabled = false, EmailEnabled = true,
            ChallengeId = TestTokens.SeededChallengeId
        };

        context.TwoFactorAuths.Add(twoFactorAuth);
        await context.SaveChangesAsync();

        var recoveryCode = $"TEST-{Guid.NewGuid():N}"[..9].ToUpperInvariant();

        context.TwoFactorRecoveryCodes.Add(new TwoFactorRecoveryCode
        {
            TwoFactorAuthId = twoFactorAuth.Id,
            CodeHash = SHA256.HashData(Encoding.UTF8.GetBytes(recoveryCode))
        });
        await context.SaveChangesAsync();

        return recoveryCode;
    }

    /// <summary>A full person token's identity, carrying the person's current stamp as an issued one does.</summary>
    private static IdentityUser FullIdentity(Person person) =>
        new(person.PublicId, (int)Roles.SystemAdmin) { SecurityStamp = TestTokens.StampOf(person.PublicId) };

    private static IdentityUser SubjectRightsIdentity(Person person) =>
        new(person.PublicId, (int)Roles.SystemAdmin)
        {
            SubjectRightsOnly = true, SecurityStamp = TestTokens.StampOf(person.PublicId)
        };

    private static IdentityUser ChallengeIdentity(Person person) =>
        new(person.PublicId, (int)Roles.SystemAdmin)
        {
            MfaPending = true,
            ChallengeId = TestTokens.SeededChallengeId,
            SecurityStamp = TestTokens.StampOf(person.PublicId)
        };

    /// <summary>
    ///     An application token for <paramref name="credential" />, built as the host builds one but
    ///     under an issuer and audience of the test's choosing.
    /// </summary>
    private static string ApplicationToken(
        Scope scope, Domain.Entities.Application application, ApplicationCredential credential,
        string issuer, string audience) =>
        new JwtHandler().CreateToken(PostgresFixture.SigningKeys.ApplyTo(new JwtConfiguration(
            600,
            issuer,
            audience,
            string.Empty,
            new Dictionary<string, string>
            {
                [ApplicationTokenClaims.TokenKind] = ApplicationTokenClaims.ApplicationKind,
                [ApplicationTokenClaims.Id] = application.PublicId.ToString(),
                [ApplicationTokenClaims.ScopeId] = scope.PublicId.ToString(),
                [ApplicationTokenClaims.CredentialId] = credential.PublicId.ToString(),
                [ApplicationTokenClaims.Name] = application.Name
            })));

    /// <summary>A full person token under two audiences, Heimdall's first — which Heimdall never issues.</summary>
    private static string TwoAudienceToken(Person person) =>
        new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = TestTokens.Issuer,
            Claims = new Dictionary<string, object>
            {
                [JwtRegisteredClaimNames.Aud] = new[] { TestTokens.Audiences.Person, ForeignAudience }
            },
            Subject = new System.Security.Claims.ClaimsIdentity(
                new IdentityUserMapper(TestTokens.Audiences).ToClaims(FullIdentity(person))
                    .Select(claim => new System.Security.Claims.Claim(claim.Key, claim.Value))),
            Expires = DateTime.UtcNow.AddMinutes(10),
            SigningCredentials = UnitSigningKeys.Es256(PostgresFixture.SigningKey)
        });

    private Task<HttpOutput<DataOutput<object?>?>> ReadAsync(Person person) =>
        Gateway.GetAsync<DataOutput<object?>>($"/api/persons/{person.PublicId}");

    private Task<HttpOutput<DataOutput<DataExportCommandOutput?>?>> ExportAsync() =>
        Gateway.PostAsync<DataOutput<DataExportCommandOutput?>>("/api/auth/data-export", new { });

    private Task<HttpOutput<DataOutput<VerifyTwoFactorAuthCommandOutput?>?>> VerifyAsync(
        string challengeToken, string recoveryCode) =>
        Gateway.PostAsync<DataOutput<VerifyTwoFactorAuthCommandOutput?>>(
            "/api/auth/2fa/verify",
            new VerifyTwoFactorAuthCommand { ChallengeToken = challengeToken, RecoveryCode = recoveryCode });

    private Task<HttpOutput<DataOutput<ApplicationTokenCheckOutput?>?>> SelfAsync(string token)
    {
        ActAs(token);
        return Gateway.GetAsync<DataOutput<ApplicationTokenCheckOutput?>>("/api/applications/self");
    }

    /// <summary>Replaces the bearer token: <c>Authorize</c> only adds a header, never overwrites one.</summary>
    private void ActAs(string token)
    {
        Gateway.Client.DefaultRequestHeaders.Remove("Authorization");
        Authorize(token);
    }

    // The person audience, on the bearer path

    [FunctionalFact]
    public async Task GivenAFullTokenUnderHeimdallsIssuerAndPersonAudience_WhenReadingAPerson_ThenOk()
    {
        // Given
        var person = await Seeds.PersonAsync(db, Roles.SystemAdmin, new PersonOptions { EmailPrefix = "origin-person-ok" });
        ActAs(TestTokens.WithOrigin(FullIdentity(person), TestTokens.Issuer, TestTokens.Audiences.Person));

        // When
        var read = await ReadAsync(person);

        // Then
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
    }

    [FunctionalTheory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task GivenAFullTokenFromAnotherIssuerOrForAnotherAudience_WhenReadingAPerson_ThenUnauthorized(
        bool foreignIssuer, bool foreignAudience)
    {
        // Given a correctly signed token naming another issuer, another audience, or both
        var person = await Seeds.PersonAsync(db, Roles.SystemAdmin, new PersonOptions { EmailPrefix = "origin-person-foreign" });
        ActAs(TestTokens.WithOrigin(
            FullIdentity(person),
            foreignIssuer ? ForeignIssuer : TestTokens.Issuer,
            foreignAudience ? ForeignAudience : TestTokens.Audiences.Person));

        // When
        var read = await ReadAsync(person);

        // Then — the answer a forged signature gets
        Assert.Equal(HttpStatusCode.Unauthorized, read.StatusCode);
        Assert.Equal([PersonTokenValidator.InvalidToken], read.Body!.Errors);
    }

    [FunctionalFact]
    public async Task GivenAFullTokenUnderTheApplicationAudience_WhenReadingAPerson_ThenUnauthorized()
    {
        // Given a person's claims under the audience only application tokens carry
        var person = await Seeds.PersonAsync(db, Roles.SystemAdmin, new PersonOptions { EmailPrefix = "origin-person-application" });
        ActAs(TestTokens.WithOrigin(FullIdentity(person), TestTokens.Issuer, PostgresFixture.ApplicationTokenAudience));

        // When
        var read = await ReadAsync(person);

        // Then
        Assert.Equal(HttpStatusCode.Unauthorized, read.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenAFullTokenWithASecondAudience_WhenReadingAPerson_ThenUnauthorized()
    {
        // Given Heimdall's person audience and another beside it
        var person = await Seeds.PersonAsync(db, Roles.SystemAdmin, new PersonOptions { EmailPrefix = "origin-person-two-audiences" });
        ActAs(TwoAudienceToken(person));

        // When
        var read = await ReadAsync(person);

        // Then
        Assert.Equal(HttpStatusCode.Unauthorized, read.StatusCode);
    }

    // The subject-rights audience

    [FunctionalFact]
    public async Task GivenASubjectRightsTokenUnderHeimdallsIssuerAndItsAudience_WhenExporting_ThenOk()
    {
        // Given a restricted identity's token, as its issuer writes it
        var person = await Seeds.PersonAsync(db, Roles.SystemAdmin, new PersonOptions { EmailPrefix = "origin-rights-ok", RestrictedAt = DateTime.UtcNow.AddDays(-30) });
        ActAs(TestTokens.WithOrigin(
            SubjectRightsIdentity(person), TestTokens.Issuer, TestTokens.Audiences.SubjectRights));

        // When
        var export = await ExportAsync();

        // Then
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
    }

    [FunctionalTheory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task GivenASubjectRightsTokenFromAnotherIssuerOrForAnotherAudience_WhenExporting_ThenUnauthorized(
        bool foreignIssuer, bool foreignAudience)
    {
        // Given
        var person = await Seeds.PersonAsync(db, Roles.SystemAdmin, new PersonOptions { EmailPrefix = "origin-rights-foreign", RestrictedAt = DateTime.UtcNow.AddDays(-30) });
        ActAs(TestTokens.WithOrigin(
            SubjectRightsIdentity(person),
            foreignIssuer ? ForeignIssuer : TestTokens.Issuer,
            foreignAudience ? ForeignAudience : TestTokens.Audiences.SubjectRights));

        // When
        var export = await ExportAsync();

        // Then
        Assert.Equal(HttpStatusCode.Unauthorized, export.StatusCode);
    }

    // The challenge audience

    [FunctionalTheory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task GivenAChallengeFromAnotherIssuerOrForAnotherAudience_WhenVerifying_ThenRefusedAndTheCodeIsKept(
        bool foreignIssuer, bool foreignAudience)
    {
        // Given an outstanding challenge, and a token naming it from elsewhere
        var person = await Seeds.PersonAsync(db, Roles.SystemAdmin, new PersonOptions { EmailPrefix = "origin-challenge" });
        var recoveryCode = await SeedTwoFactorWithRecoveryCodeAsync(person);
        var foreign = TestTokens.WithOrigin(
            ChallengeIdentity(person),
            foreignIssuer ? ForeignIssuer : TestTokens.Issuer,
            foreignAudience ? ForeignAudience : TestTokens.Audiences.TwoFactorChallenge);

        // When it is redeemed with a genuine recovery code
        var refused = await VerifyAsync(foreign, recoveryCode);

        // Then
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        Assert.Contains(TwoFactorMessages.ChallengeTokenInvalid, refused.Body!.Errors);

        // And the challenge Heimdall issued, under its own issuer and audience, still redeems the code
        var accepted = await VerifyAsync(
            TestTokens.WithOrigin(
                ChallengeIdentity(person), TestTokens.Issuer, TestTokens.Audiences.TwoFactorChallenge),
            recoveryCode);

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.False(string.IsNullOrEmpty(accepted.Body!.Data!.Token));
    }

    // The application audience

    [FunctionalFact]
    public async Task GivenAnApplicationTokenUnderHeimdallsIssuerAndTheApplicationAudience_WhenCheckingSelf_ThenOk()
    {
        // Given
        var (scope, _, application) = await SeedOwnedApplicationAsync(db);
        var (credential, _) = await SeedCredentialAsync(db, application);

        // When
        var check = await SelfAsync(ApplicationToken(
            scope, application, credential, TestTokens.Issuer, PostgresFixture.ApplicationTokenAudience));

        // Then
        Assert.Equal(HttpStatusCode.OK, check.StatusCode);
        Assert.Equal(credential.PublicId, check.Body!.Data!.CredentialId);
    }

    [FunctionalTheory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task GivenAnApplicationTokenFromAnotherIssuerOrForAnotherAudience_WhenCheckingSelf_ThenUnauthorized(
        bool foreignIssuer, bool foreignAudience)
    {
        // Given
        var (scope, _, application) = await SeedOwnedApplicationAsync(db);
        var (credential, _) = await SeedCredentialAsync(db, application);

        // When
        var check = await SelfAsync(ApplicationToken(
            scope, application, credential,
            foreignIssuer ? ForeignIssuer : TestTokens.Issuer,
            foreignAudience ? ForeignAudience : PostgresFixture.ApplicationTokenAudience));

        // Then
        Assert.Equal(HttpStatusCode.Unauthorized, check.StatusCode);
        Assert.Equal([ApplicationTokenMessages.TokenNotAccepted], check.Body!.Errors);
    }
}
