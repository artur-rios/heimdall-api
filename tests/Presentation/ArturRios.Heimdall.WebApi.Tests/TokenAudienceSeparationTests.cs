using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using ArturRios.Configuration.Enums;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Heimdall.WebApi.Security;
using ArturRios.Heimdall.WebApi.Tests.Support;
using ArturRios.Output;
using ArturRios.Util.Hashing;
using ArturRios.Util.Http;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Functional;

namespace ArturRios.Heimdall.WebApi.Tests;

// Functional tests for the separate audiences of the 2FA challenge token and the subject-rights-only
// token (TH-06, TH-35). Over the real pipeline: each is issued under its own audience and still works
// where it belongs — the challenge at verification and resend, the restricted token at the rights
// endpoints — while the full token keeps the person audience. Then the other half: since Heimdall's
// middleware checks no audience, a token whose audience and claims disagree is refused, not read as
// whichever half suits the caller. Those tokens are minted with the host's real key, so nothing but
// the disagreement is wrong with them.
[Collection(nameof(FunctionalCollection))]
public class TokenAudienceSeparationTests(PostgresFixture db) : WebApiTest<Program>(EnvironmentType.Local)
{
    private const string Password = "Str0ng-Audience-Pass!";

    private static string UniqueEmail(string prefix) => $"{prefix}-{Guid.NewGuid():N}@test.local";

    private static string AudienceOf(string token) =>
        Assert.Single(new JwtSecurityTokenHandler().ReadJwtToken(token).Audiences);

    private async Task<Person> SeedPersonAsync(string prefix, DateTime? restrictedAt = null)
    {
        await using var context = db.CreateContext();

        var person = new Person
        {
            PublicId = Guid.NewGuid(),
            Name = "Audience",
            Email = UniqueEmail(prefix),
            PasswordHash = Hash.EncodeWithRandomSalt(Password, out var salt),
            Salt = salt,
            RoleId = (long)Roles.SystemAdmin,
            EmailVerified = true,
            ProcessingRestrictedAt = restrictedAt,
            RestrictionGround = restrictedAt is null ? null : (int)RestrictionGrounds.AccuracyContested
        };

        context.Persons.Add(person);
        await context.SaveChangesAsync();

        return person;
    }

    /// <summary>
    ///     An active Email configuration, so a login mails a code and the resend has one to retire,
    ///     holding one recovery code — which login leaves alone — to redeem the challenge with.
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

    private Task<HttpOutput<DataOutput<LoginCommandOutput?>?>> LoginAsync(string email) =>
        Gateway.PostAsync<DataOutput<LoginCommandOutput?>>(
            "/api/auth/login", new LoginCommand { Email = email, Password = Password });

    private Task<HttpOutput<DataOutput<VerifyTwoFactorAuthCommandOutput?>?>> VerifyWithRecoveryCodeAsync(
        string challengeToken, string recoveryCode) =>
        Gateway.PostAsync<DataOutput<VerifyTwoFactorAuthCommandOutput?>>(
            "/api/auth/2fa/verify",
            new VerifyTwoFactorAuthCommand { ChallengeToken = challengeToken, RecoveryCode = recoveryCode });

    private Task<HttpOutput<DataOutput<ResendTwoFactorChallengeCodeCommandOutput?>?>> ResendAsync(
        string challengeToken) =>
        Gateway.PostAsync<DataOutput<ResendTwoFactorChallengeCodeCommandOutput?>>(
            "/api/auth/2fa/challenge/resend",
            new ResendTwoFactorChallengeCodeCommand { ChallengeToken = challengeToken });

    private Task<HttpOutput<DataOutput<object?>?>> ReadAsync(Person person) =>
        Gateway.GetAsync<DataOutput<object?>>($"/api/persons/{person.PublicId}");

    private Task<HttpOutput<DataOutput<DataExportCommandOutput?>?>> ExportAsync() =>
        Gateway.PostAsync<DataOutput<DataExportCommandOutput?>>("/api/auth/data-export", new { });

    /// <summary>Replaces the bearer token: <c>Authorize</c> only adds a header, never overwrites one.</summary>
    private void ActAs(string token)
    {
        Gateway.Client.DefaultRequestHeaders.Remove("Authorization");
        Authorize(token);
    }

    // Each token where it belongs

    [FunctionalFact]
    public async Task GivenATwoFactorLogin_WhenResentAndRedeemed_ThenOnlyTheFinalTokenCarriesThePersonAudience()
    {
        // Given a 2FA-gated person who signs in with their password
        var person = await SeedPersonAsync("audience-2fa");
        var recoveryCode = await SeedTwoFactorWithRecoveryCodeAsync(person);

        var login = await LoginAsync(person.Email);

        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.True(login.Body!.Data!.RequiresTwoFactor);
        var challengeToken = login.Body.Data.ChallengeToken!;

        // Then — the challenge carries its own audience, not the full token's
        Assert.Equal(TestTokens.Audiences.TwoFactorChallenge, AudienceOf(challengeToken));

        // When the email code is resent with it (UC-46)
        var resend = await ResendAsync(challengeToken);

        // Then it is still accepted there
        Assert.Equal(HttpStatusCode.OK, resend.StatusCode);
        Assert.Contains(TwoFactorMessages.ChallengeCodeResent, resend.Body!.Messages);

        // When it is redeemed (UC-38)
        var verify = await VerifyWithRecoveryCodeAsync(challengeToken, recoveryCode);

        // Then — a full token, under the person audience, that works like any other
        Assert.Equal(HttpStatusCode.OK, verify.StatusCode);
        var fullToken = verify.Body!.Data!.Token!;
        Assert.Equal(TestTokens.Audiences.Person, AudienceOf(fullToken));

        ActAs(fullToken);
        Assert.Equal(HttpStatusCode.OK, (await ReadAsync(person)).StatusCode);
    }

    [FunctionalFact]
    public async Task GivenALoginChallenge_WhenUsedAsABearerElsewhere_ThenTheGuardStillRefusesIt()
    {
        // Given a challenge from a real login
        var person = await SeedPersonAsync("audience-guard");
        await SeedTwoFactorWithRecoveryCodeAsync(person);
        var login = await LoginAsync(person.Email);
        ActAs(login.Body!.Data!.ChallengeToken!);

        // When it is presented on an ordinary endpoint
        var read = await ReadAsync(person);

        // Then — refused by MfaPendingGuardFilter as before: the token is still read as a pending
        // challenge under its own audience, and confined by the filter
        Assert.Equal(HttpStatusCode.Unauthorized, read.StatusCode);
        Assert.Contains(
            "A two-factor challenge is still pending completion; this token is not valid here.",
            read.Body!.Errors);
    }

    [FunctionalFact]
    public async Task GivenARestrictedSignIn_WhenItsTokenIsUsed_ThenItCarriesItsOwnAudienceAndServesOnlyTheRights()
    {
        // Given a restricted identity (NFR-24) signing in
        var person = await SeedPersonAsync("audience-rights", restrictedAt: DateTime.UtcNow.AddDays(-30));

        var login = await LoginAsync(person.Email);

        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.True(login.Body!.Data!.ProcessingRestricted);
        var rightsOnly = login.Body.Data.Token!;

        // Then — the subject-rights audience, not the full token's
        Assert.Equal(TestTokens.Audiences.SubjectRights, AudienceOf(rightsOnly));

        // Then — the rights endpoints still take it, and everything else still refuses it
        ActAs(rightsOnly);
        Assert.Equal(HttpStatusCode.OK, (await ExportAsync()).StatusCode);

        var elsewhere = await ReadAsync(person);
        Assert.Equal(HttpStatusCode.Forbidden, elsewhere.StatusCode);
        Assert.Contains(SubjectRightsOnlyGuardFilter.SubjectRightsOnly, elsewhere.Body!.Errors);
    }

    // A token whose audience and claims disagree

    [FunctionalFact]
    public async Task GivenAPendingMarkUnderThePersonAudience_WhenPresentedForVerification_ThenUnauthorized()
    {
        // Given an outstanding challenge, and a token naming it with mfaPending under the full token's
        // audience — not what the challenge issuer writes
        var person = await SeedPersonAsync("audience-crafted-pending");
        var recoveryCode = await SeedTwoFactorWithRecoveryCodeAsync(person);

        var crafted = TestTokens.WithAudience(
            new IdentityUser(person.PublicId, (int)Roles.SystemAdmin)
            {
                MfaPending = true, ChallengeId = TestTokens.SeededChallengeId
            },
            TestTokens.Audiences.Person);

        // When it is redeemed with a genuine recovery code
        var verify = await VerifyWithRecoveryCodeAsync(crafted, recoveryCode);

        // Then
        Assert.Equal(HttpStatusCode.Unauthorized, verify.StatusCode);
        Assert.Contains(TwoFactorMessages.ChallengeTokenInvalid, verify.Body!.Errors);

        // And as a bearer token it authenticates no one
        ActAs(crafted);
        Assert.Equal(HttpStatusCode.Unauthorized, (await ReadAsync(person)).StatusCode);
    }

    [FunctionalFact]
    public async Task GivenARestrictedMarkUnderThePersonAudience_WhenExporting_ThenUnauthorized()
    {
        // Given a token marked subject-rights-only under the full token's audience
        var person = await SeedPersonAsync("audience-crafted-rights");

        ActAs(TestTokens.WithAudience(
            new IdentityUser(person.PublicId, (int)Roles.SystemAdmin) { SubjectRightsOnly = true },
            TestTokens.Audiences.Person));

        // When
        var export = await ExportAsync();

        // Then
        Assert.Equal(HttpStatusCode.Unauthorized, export.StatusCode);
    }

    [FunctionalTheory]
    [InlineData(PersonTokenAudiences.TwoFactorChallengeSuffix)]
    [InlineData(PersonTokenAudiences.SubjectRightsSuffix)]
    public async Task GivenAnUnmarkedTokenUnderARestrictedAudience_WhenCallingAnOrdinaryEndpoint_ThenUnauthorized(
        string suffix)
    {
        // Given a token with no mark at all, under the challenge or the subject-rights audience: the
        // audience says it is not a sign-in, so it is not read as one
        var person = await SeedPersonAsync("audience-crafted-unmarked");

        ActAs(TestTokens.WithAudience(
            new IdentityUser(person.PublicId, (int)Roles.SystemAdmin),
            TestTokens.Audiences.Person + suffix));

        // When
        var read = await ReadAsync(person);

        // Then
        Assert.Equal(HttpStatusCode.Unauthorized, read.StatusCode);
    }
}
