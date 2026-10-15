using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using ArturRios.Configuration.Enums;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Command.Services;
using ArturRios.Heimdall.Data.Persistence;
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
using Microsoft.EntityFrameworkCore;
using OtpNet;

namespace ArturRios.Heimdall.WebApi.Tests;

// Functional tests for the security stamp (FR-AU-11, Threat Model TH-36): every person token carries
// the person's stamp, a credential change replaces it, and ActorLivenessFilter refuses a token whose
// stamp is missing or no longer the person's. Each event is driven through its real endpoint with a
// token issued before it, which must be refused afterwards, while a token issued after it works. The
// three person token kinds are covered: the full token, the 2FA challenge token and the
// subject-rights-only token.
[Collection(nameof(FunctionalCollection))]
public class SecurityStampTests(PostgresFixture db) : WebApiTest<Program>(EnvironmentType.Local)
{
    private const string Password = "Str0ng-Stamp-Pass!";
    private const string NewPassword = "N3w-Str0ng-Stamp-Pass!";

    private static string UniqueEmail(string prefix) => $"{prefix}-{Guid.NewGuid():N}@test.local";

    private static string CurrentTotpCode(string base32Secret) =>
        new Totp(Base32Encoding.ToBytes(base32Secret)).ComputeTotp();

    private static string? StampClaimOf(string token) =>
        new JwtSecurityTokenHandler().ReadJwtToken(token).Claims
            .FirstOrDefault(claim => claim.Type == IdentityUserMapper.SecurityStampClaim)?.Value;

    private async Task<Person> SeedPersonAsync(
        string prefix, Roles role = Roles.SystemAdmin, DateTime? restrictedAt = null)
    {
        await using var context = db.CreateContext();

        var person = new Person
        {
            PublicId = Guid.NewGuid(),
            Name = "Stamp",
            Email = UniqueEmail(prefix),
            PasswordHash = Hash.EncodeWithRandomSalt(Password, out var salt),
            Salt = salt,
            RoleId = (long)role,
            EmailVerified = true,
            ProcessingRestrictedAt = restrictedAt,
            RestrictionGround = restrictedAt is null ? null : (int)RestrictionGrounds.AccuracyContested
        };

        context.Persons.Add(person);
        await context.SaveChangesAsync();

        return person;
    }

    private async Task<Guid> StoredStampAsync(Person person)
    {
        await using var context = db.CreateContext();

        return await context.Persons
            .Where(x => x.Id == person.Id)
            .Select(x => x.SecurityStamp)
            .SingleAsync();
    }

    /// <summary>Writes a reset token directly, returning its plaintext, as UC-13's own tests do.</summary>
    private async Task<string> SeedResetTokenAsync(Person person)
    {
        var plaintext = $"token-{Guid.NewGuid():N}";

        await using var context = db.CreateContext();
        context.PasswordResetTokens.Add(new PasswordResetToken
        {
            PersonId = person.Id,
            TokenHash = SingleUseTokenHash.Of(plaintext),
            ExpiresAt = DateTime.UtcNow.AddHours(1)
        });
        await context.SaveChangesAsync();

        return plaintext;
    }

    private async Task<TwoFactorAuth> SeedActiveTwoFactorAsync(Person person)
    {
        await using var context = db.CreateContext();

        var twoFactorAuth = new TwoFactorAuth
        {
            PersonId = person.Id, IsActive = true, AppEnabled = false, EmailEnabled = true,
            ChallengeId = TestTokens.SeededChallengeId
        };

        context.TwoFactorAuths.Add(twoFactorAuth);
        await context.SaveChangesAsync();

        return twoFactorAuth;
    }

    private async Task<string> SeedRecoveryCodeAsync(TwoFactorAuth twoFactorAuth)
    {
        var plaintext = $"TEST-{Guid.NewGuid():N}"[..9];

        await using var context = db.CreateContext();
        context.TwoFactorRecoveryCodes.Add(new TwoFactorRecoveryCode
        {
            TwoFactorAuthId = twoFactorAuth.Id, CodeHash = SHA256.HashData(Encoding.UTF8.GetBytes(plaintext))
        });
        await context.SaveChangesAsync();

        return plaintext;
    }

    /// <summary>Replaces the bearer token: <c>Authorize</c> sets the header, this also clears it first.</summary>
    private void ActAs(string token)
    {
        Gateway.Client.DefaultRequestHeaders.Remove("Authorization");
        Authorize(token);
    }

    private Task<HttpOutput<DataOutput<object?>?>> ReadAsync(Person person) =>
        Gateway.GetAsync<DataOutput<object?>>($"/api/persons/{person.PublicId}");

    private Task<HttpOutput<DataOutput<LoginCommandOutput?>?>> LoginAsync(string email, string password = Password) =>
        Gateway.PostAsync<DataOutput<LoginCommandOutput?>>(
            "/api/auth/login", new LoginCommand { Email = email, Password = password });

    private Task<HttpOutput<DataOutput<ResetPasswordCommandOutput?>?>> ResetAsync(string token) =>
        Gateway.PostAsync<DataOutput<ResetPasswordCommandOutput?>>(
            "/api/auth/password-reset", new ResetPasswordCommand { Token = token, NewPassword = NewPassword });

    private Task<HttpOutput<DataOutput<VerifyTwoFactorAuthCommandOutput?>?>> VerifyAsync(
        string challengeToken, string recoveryCode) =>
        Gateway.PostAsync<DataOutput<VerifyTwoFactorAuthCommandOutput?>>(
            "/api/auth/2fa/verify",
            new VerifyTwoFactorAuthCommand { ChallengeToken = challengeToken, RecoveryCode = recoveryCode });

    private Task<HttpOutput<DataOutput<DataExportCommandOutput?>?>> ExportAsync() =>
        Gateway.PostAsync<DataOutput<DataExportCommandOutput?>>("/api/auth/data-export", new { });

    private static void AssertRevoked<T>(HttpOutput<DataOutput<T>?> response)
    {
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(ActorLivenessFilter.ActorTokenRevoked, response.Body!.Errors);
    }

    // Every person token carries the stamp

    [FunctionalFact]
    public async Task GivenASignIn_WhenTheTokenIsRead_ThenItCarriesThePersonsCurrentStamp()
    {
        // Given
        var person = await SeedPersonAsync("stamp-claim");

        // When
        var login = await LoginAsync(person.Email);

        // Then
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.Equal((await StoredStampAsync(person)).ToString(), StampClaimOf(login.Body!.Data!.Token!));
    }

    [FunctionalFact]
    public async Task GivenATokenWithNoStamp_WhenUsed_ThenItIsRefused()
    {
        // Given a person token minted with the host's real key but without the stamp claim, as every
        // token issued before the stamp existed is
        var person = await SeedPersonAsync("stamp-missing");
        ActAs(TestTokens.WithAudience(
            new IdentityUser(person.PublicId, (int)Roles.SystemAdmin), TestTokens.Audiences.Person));

        // When
        var response = await ReadAsync(person);

        // Then
        AssertRevoked(response);
    }

    [FunctionalFact]
    public async Task GivenATokenWithAnotherStamp_WhenUsed_ThenItIsRefused()
    {
        // Given
        var person = await SeedPersonAsync("stamp-other");
        ActAs(TestTokens.WithAudience(
            new IdentityUser(person.PublicId, (int)Roles.SystemAdmin) { SecurityStamp = Guid.NewGuid() },
            TestTokens.Audiences.Person));

        // When
        var response = await ReadAsync(person);

        // Then
        AssertRevoked(response);
    }

    // Each credential event revokes the tokens issued before it

    [FunctionalFact]
    public async Task GivenATokenIssuedBeforeAPasswordReset_WhenUsedAfterIt_ThenItIsRefusedAndAFreshOneWorks()
    {
        // Given a signed-in person, whose token works
        var person = await SeedPersonAsync("stamp-reset");
        var before = (await LoginAsync(person.Email)).Body!.Data!.Token!;
        ActAs(before);
        Assert.Equal(HttpStatusCode.OK, (await ReadAsync(person)).StatusCode);

        // When the password is reset through UC-13
        var reset = await ResetAsync(await SeedResetTokenAsync(person));
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);

        // Then the earlier token is refused
        AssertRevoked(await ReadAsync(person));

        // Then — a token issued after the reset works
        var after = await LoginAsync(person.Email, NewPassword);
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
        ActAs(after.Body!.Data!.Token!);
        Assert.Equal(HttpStatusCode.OK, (await ReadAsync(person)).StatusCode);
    }

    [FunctionalFact]
    public async Task GivenATokenIssuedBeforeAnEmailChange_WhenUsedAfterIt_ThenItIsRefusedAndAFreshOneWorks()
    {
        // Given a person holding a token, and an administrator who changes the person's address
        var person = await SeedPersonAsync("stamp-email", Roles.User);
        var before = TestTokens.For(person.PublicId, (int)Roles.User);
        var admin = await SeedPersonAsync("stamp-email-admin");

        // When
        ActAs(TestTokens.For(admin.PublicId, (int)Roles.SystemAdmin));
        var update = await Gateway.PutAsync<DataOutput<UpdatePersonCommandOutput?>>(
            $"/api/persons/{person.PublicId}",
            new UpdatePersonCommand { Name = person.Name, Email = UniqueEmail("stamp-email-new") });
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);

        // Then the person's earlier token is refused, and the administrator's own still works
        ActAs(before);
        AssertRevoked(await ReadAsync(person));

        ActAs(TestTokens.For(admin.PublicId, (int)Roles.SystemAdmin));
        Assert.Equal(HttpStatusCode.OK, (await ReadAsync(person)).StatusCode);

        // Then — a token issued after the change works
        ActAs(TestTokens.For(person.PublicId, (int)Roles.User));
        Assert.Equal(HttpStatusCode.OK, (await ReadAsync(person)).StatusCode);
    }

    [FunctionalFact]
    public async Task GivenANameOnlyUpdate_WhenTheEarlierTokenIsUsed_ThenItStillWorks()
    {
        // Given — a change that touches no credential
        var person = await SeedPersonAsync("stamp-name");
        var token = TestTokens.For(person.PublicId, (int)Roles.SystemAdmin);
        var stamp = await StoredStampAsync(person);
        ActAs(token);

        // When
        var update = await Gateway.PutAsync<DataOutput<UpdatePersonCommandOutput?>>(
            $"/api/persons/{person.PublicId}", new UpdatePersonCommand { Name = "Renamed", Email = person.Email });

        // Then
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        Assert.Equal(stamp, await StoredStampAsync(person));
        Assert.Equal(HttpStatusCode.OK, (await ReadAsync(person)).StatusCode);
    }

    [FunctionalFact]
    public async Task GivenATokenIssuedBeforeTwoFactorIsConfirmed_WhenUsedAfterIt_ThenItIsRefusedAndAFreshOneWorks()
    {
        // Given a person who starts App setup with their token
        var person = await SeedPersonAsync("stamp-2fa-on");
        var before = TestTokens.For(person.PublicId, (int)Roles.SystemAdmin);
        ActAs(before);

        var enable = await Gateway.PostAsync<DataOutput<EnableTwoFactorAuthCommandOutput?>>(
            "/api/auth/2fa/enable",
            new EnableTwoFactorAuthCommand { Methods = ["App"], CurrentPassword = Password });
        Assert.Equal(HttpStatusCode.OK, enable.StatusCode);

        var uri = enable.Body!.Data!.OtpAuthUri!;
        var secret = uri[(uri.IndexOf("secret=", StringComparison.Ordinal) + "secret=".Length)..].Split('&')[0];

        // When setup is confirmed (UC-37)
        var confirm = await Gateway.PostAsync<DataOutput<ConfirmTwoFactorAuthCommandOutput?>>(
            "/api/auth/2fa/confirm", new ConfirmTwoFactorAuthCommand { AppCode = CurrentTotpCode(secret) });
        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);

        // Then the token that confirmed it is refused from the next request on
        AssertRevoked(await ReadAsync(person));

        // Then — a token issued after it works
        ActAs(TestTokens.For(person.PublicId, (int)Roles.SystemAdmin));
        Assert.Equal(HttpStatusCode.OK, (await ReadAsync(person)).StatusCode);
    }

    [FunctionalFact]
    public async Task GivenATokenIssuedBeforeTwoFactorIsDisabled_WhenUsedAfterIt_ThenItIsRefusedAndAFreshOneWorks()
    {
        // Given
        var person = await SeedPersonAsync("stamp-2fa-off");
        var twoFactorAuth = await SeedActiveTwoFactorAsync(person);
        var recoveryCode = await SeedRecoveryCodeAsync(twoFactorAuth);
        ActAs(TestTokens.For(person.PublicId, (int)Roles.SystemAdmin));

        // When (UC-39)
        var disable = await Gateway.PostAsync<DataOutput<DisableTwoFactorAuthCommandOutput?>>(
            "/api/auth/2fa/disable",
            new DisableTwoFactorAuthCommand { Password = Password, RecoveryCode = recoveryCode });
        Assert.Equal(HttpStatusCode.OK, disable.StatusCode);

        // Then
        AssertRevoked(await ReadAsync(person));

        ActAs(TestTokens.For(person.PublicId, (int)Roles.SystemAdmin));
        Assert.Equal(HttpStatusCode.OK, (await ReadAsync(person)).StatusCode);
    }

    [FunctionalFact]
    public async Task GivenATokenIssuedBeforeRecoveryCodesAreRegenerated_WhenUsedAfterIt_ThenItIsRefused()
    {
        // Given
        var person = await SeedPersonAsync("stamp-2fa-codes");
        var twoFactorAuth = await SeedActiveTwoFactorAsync(person);
        var recoveryCode = await SeedRecoveryCodeAsync(twoFactorAuth);
        ActAs(TestTokens.For(person.PublicId, (int)Roles.SystemAdmin));

        // When (UC-40)
        var regenerate = await Gateway.PostAsync<DataOutput<RegenerateRecoveryCodesCommandOutput?>>(
            "/api/auth/2fa/recovery-codes/regenerate",
            new RegenerateRecoveryCodesCommand { RecoveryCode = recoveryCode });
        Assert.Equal(HttpStatusCode.OK, regenerate.StatusCode);

        // Then
        AssertRevoked(await ReadAsync(person));
    }

    // The challenge token and the subject-rights-only token

    [FunctionalFact]
    public async Task GivenATwoFactorSignIn_WhenRedeemed_ThenTheChallengeAndTheFullTokenCarryTheStampAndWork()
    {
        // Given a 2FA-gated person who signs in with their password
        var person = await SeedPersonAsync("stamp-challenge");
        var recoveryCode = await SeedRecoveryCodeAsync(await SeedActiveTwoFactorAsync(person));
        var stamp = (await StoredStampAsync(person)).ToString();

        var login = await LoginAsync(person.Email);
        var challengeToken = login.Body!.Data!.ChallengeToken!;
        Assert.Equal(stamp, StampClaimOf(challengeToken));

        // When the challenge is redeemed
        var verify = await VerifyAsync(challengeToken, recoveryCode);

        // Then the full token carries the same stamp and works
        Assert.Equal(HttpStatusCode.OK, verify.StatusCode);
        Assert.Equal(stamp, StampClaimOf(verify.Body!.Data!.Token!));

        ActAs(verify.Body.Data.Token!);
        Assert.Equal(HttpStatusCode.OK, (await ReadAsync(person)).StatusCode);
    }

    [FunctionalFact]
    public async Task GivenAChallengeIssuedBeforeAPasswordReset_WhenRedeemedAfterIt_ThenUnauthorized()
    {
        // Given a challenge obtained with the old password, still outstanding
        var person = await SeedPersonAsync("stamp-challenge-reset");
        var recoveryCode = await SeedRecoveryCodeAsync(await SeedActiveTwoFactorAsync(person));
        var challengeToken = (await LoginAsync(person.Email)).Body!.Data!.ChallengeToken!;

        // When the password is reset, and the challenge is then redeemed with a genuine factor
        Assert.Equal(HttpStatusCode.OK, (await ResetAsync(await SeedResetTokenAsync(person))).StatusCode);
        var verify = await VerifyAsync(challengeToken, recoveryCode);

        // Then
        Assert.Equal(HttpStatusCode.Unauthorized, verify.StatusCode);
        Assert.Contains(TwoFactorMessages.ChallengeTokenInvalid, verify.Body!.Errors);
    }

    [FunctionalFact]
    public async Task GivenASubjectRightsToken_WhenUsedBeforeAndAfterAPasswordReset_ThenItWorksAndThenIsRefused()
    {
        // Given a restricted person, signed in for their own rights
        var person = await SeedPersonAsync("stamp-rights", restrictedAt: DateTime.UtcNow.AddDays(-1));
        var login = await LoginAsync(person.Email);
        var rightsToken = login.Body!.Data!.Token!;
        Assert.Equal((await StoredStampAsync(person)).ToString(), StampClaimOf(rightsToken));

        ActAs(rightsToken);
        Assert.Equal(HttpStatusCode.OK, (await ExportAsync()).StatusCode);

        // When
        Assert.Equal(HttpStatusCode.OK, (await ResetAsync(await SeedResetTokenAsync(person))).StatusCode);

        // Then
        AssertRevoked(await ExportAsync());
    }

    // The stamp is written only by the targeted rotation

    [FunctionalFact]
    public async Task GivenACopyReadBeforeARotation_WhenSavedAsAWholeRow_ThenTheRotatedStampStands()
    {
        // Given one request holding the person as it was, while another rotates the stamp
        var person = await SeedPersonAsync("stamp-whole-row");

        await using var stale = db.CreateContext();
        var staleCopy = await stale.Persons.SingleAsync(x => x.Id == person.Id);

        await using var rotating = db.CreateContext();
        var current = await rotating.Persons.SingleAsync(x => x.Id == person.Id);
        await new AtomicWrites(rotating).RotateSecurityStampAsync(current);

        // When the first request saves its copy the way the repositories do — every column
        staleCopy.Name = "Saved later";
        stale.Persons.Update(staleCopy);
        await stale.SaveChangesAsync();

        // Then the revocation is not undone, and the rest of the row was still written
        await using var check = db.CreateContext();
        var stored = await check.Persons.SingleAsync(x => x.Id == person.Id);
        Assert.Equal(current.SecurityStamp, stored.SecurityStamp);
        Assert.NotEqual(person.SecurityStamp, stored.SecurityStamp);
        Assert.Equal("Saved later", stored.Name);
    }
}
