using System.Net;
using System.Security.Cryptography;
using System.Text;
using ArturRios.Configuration.Enums;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Heimdall.WebApi.Tests.Support;
using ArturRios.Output;
using ArturRios.Util.Hashing;
using ArturRios.Util.Http;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Functional;
using Microsoft.EntityFrameworkCore;
using OtpNet;

namespace ArturRios.Heimdall.WebApi.Tests;

// Functional tests for POST /api/auth/2fa/disable (UC-39, FR-2F-11): the main flow via an app code,
// an email code, and a recovery code, each confirming the TWO_FACTOR_AUTH row and its recovery codes
// are actually gone from the database afterward; AF-39a (404, not active); AF-39b (401, wrong
// password); AF-39c (401, invalid second factor); and the 401 an unauthenticated caller gets.
[Collection(nameof(FunctionalCollection))]
public class AuthControllerDisableTwoFactorAuthTests(PostgresFixture db) : WebApiTest<Program>(EnvironmentType.Local)
{
    private const string Password = "Str0ng-2fa-Pass!";
    private const string EmailCode = "123456";

    private static string UniqueEmail(string prefix) => $"{prefix}-{Guid.NewGuid():N}@test.local";

    private static string CurrentTotpCode(string base32Secret) =>
        new Totp(Base32Encoding.ToBytes(base32Secret)).ComputeTotp();

    private async Task<Person> SeedPersonAsync(Roles role, string email)
    {
        await using var context = db.CreateContext();
        var person = new Person
        {
            PublicId = Guid.NewGuid(),
            Name = $"{role}",
            Email = email,
            PasswordHash = Hash.EncodeWithRandomSalt(Password, out var salt),
            Salt = salt,
            RoleId = (long)role,
            EmailVerified = true
        };
        context.Persons.Add(person);
        await context.SaveChangesAsync();
        return person;
    }

    private async Task<TwoFactorAuth> SeedActiveAsync(Person person, bool appEnabled, bool emailEnabled,
        byte[]? totpSecretEncrypted = null)
    {
        await using var context = db.CreateContext();
        var twoFactorAuth = new TwoFactorAuth
        {
            PersonId = person.Id,
            IsActive = true,
            AppEnabled = appEnabled,
            EmailEnabled = emailEnabled,
            TotpSecretEncrypted = totpSecretEncrypted
        };
        context.TwoFactorAuths.Add(twoFactorAuth);
        await context.SaveChangesAsync();
        return twoFactorAuth;
    }

    private async Task SeedEmailCodeAsync(long twoFactorAuthId, string code, bool used = false)
    {
        await using var context = db.CreateContext();
        var codeHash = Hash.EncodeWithRandomSalt(code, out var salt);
        context.TwoFactorEmailCodes.Add(new TwoFactorEmailCode
        {
            TwoFactorAuthId = twoFactorAuthId,
            CodeHash = codeHash,
            Salt = salt,
            ExpiresAt = DateTime.UtcNow.AddMinutes(10),
            Used = used
        });
        await context.SaveChangesAsync();
    }

    private async Task<string> SeedRecoveryCodeAsync(long twoFactorAuthId, bool used = false)
    {
        var plaintext = $"TEST-{Guid.NewGuid():N}"[..9];
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(plaintext));

        await using var context = db.CreateContext();
        context.TwoFactorRecoveryCodes.Add(new TwoFactorRecoveryCode
        {
            TwoFactorAuthId = twoFactorAuthId, CodeHash = hash, Used = used, UsedAt = used ? DateTime.UtcNow : null
        });
        await context.SaveChangesAsync();

        return plaintext;
    }

    private async Task<TwoFactorAuth?> ConfigurationForAsync(Person person)
    {
        await using var context = db.CreateContext();

        return await context.TwoFactorAuths.FirstOrDefaultAsync(x => x.PersonId == person.Id);
    }

    private async Task<int> RecoveryCodeCountForAsync(long twoFactorAuthId)
    {
        await using var context = db.CreateContext();

        return await context.TwoFactorRecoveryCodes.CountAsync(x => x.TwoFactorAuthId == twoFactorAuthId);
    }

    private Task<HttpOutput<DataOutput<DisableTwoFactorAuthCommandOutput?>?>> DisableAsync(
        string? password = Password, string? code = null, string? recoveryCode = null) =>
        Gateway.PostAsync<DataOutput<DisableTwoFactorAuthCommandOutput?>>(
            "/api/auth/2fa/disable",
            new DisableTwoFactorAuthCommand
            {
                Password = password ?? string.Empty, Code = code, RecoveryCode = recoveryCode
            });

    [FunctionalFact]
    public async Task GivenValidPasswordAndAppCode_WhenPostDisable_ThenTwoFactorAuthAndRecoveryCodesAreRemoved()
    {
        // Given an App-only configuration enabled and confirmed for real, so the stored TOTP secret
        // is genuinely Data-Protection-encrypted, plus a recovery code issued alongside it
        var person = await SeedPersonAsync(Roles.SystemAdmin, UniqueEmail("app-disable"));
        Authorize(TestTokens.For(person.PublicId, (int)Roles.SystemAdmin));
        var secret = await EnableAppSecretAsync(person);
        var twoFactorAuth = (await ConfigurationForAsync(person))!;
        await SeedRecoveryCodeAsync(twoFactorAuth.Id);

        // When
        var response = await DisableAsync(code: CurrentTotpCode(secret));

        // Then — response
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Body!.Data!.Disabled);
        Assert.Contains(TwoFactorMessages.Disabled, response.Body.Messages);

        // Then — database state: the row and every recovery code are gone
        Assert.Null(await ConfigurationForAsync(person));
        Assert.Equal(0, await RecoveryCodeCountForAsync(twoFactorAuth.Id));
    }

    /// <summary>
    ///     Replaces the pending row's <c>TotpSecretEncrypted</c> with one genuinely produced by the
    ///     real <c>/2fa/enable</c> + <c>/2fa/confirm</c> endpoints, then reactivates it as an App-only
    ///     configuration — needed because <c>DisableTwoFactorAuthCommandHandler</c> unprotects the
    ///     secret for real via Data Protection, unlike the unit tests' mocked protector.
    /// </summary>
    private async Task<string> EnableAppSecretAsync(Person person)
    {
        var enable = await Gateway.PostAsync<DataOutput<EnableTwoFactorAuthCommandOutput?>>(
            "/api/auth/2fa/enable", new EnableTwoFactorAuthCommand { Methods = ["App"] });
        Assert.Equal(HttpStatusCode.OK, enable.StatusCode);

        var uri = enable.Body!.Data!.OtpAuthUri!;
        var afterSecret = uri[(uri.IndexOf("secret=", StringComparison.Ordinal) + "secret=".Length)..];
        var ampersandIndex = afterSecret.IndexOf('&');
        var secret = ampersandIndex < 0 ? afterSecret : afterSecret[..ampersandIndex];

        var confirm = await Gateway.PostAsync<DataOutput<ConfirmTwoFactorAuthCommandOutput?>>(
            "/api/auth/2fa/confirm", new ConfirmTwoFactorAuthCommand { AppCode = CurrentTotpCode(secret) });
        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);

        // The confirmation just spent the current time step, and an app code is good once. The
        // caller of this helper goes on to present a code from the same step microseconds later,
        // which no real client does; forget the step so the test exercises its own subject rather
        // than the replay guard. See PostgresFixture.ForgetLastTotpStepAsync.
        await db.ForgetLastTotpStepAsync(person.PublicId);

        return secret;
    }

    [FunctionalFact]
    public async Task GivenValidPasswordAndEmailCode_WhenPostDisable_ThenTwoFactorAuthIsRemoved()
    {
        // Given an active email-only configuration and a live email code
        var person = await SeedPersonAsync(Roles.SystemAdmin, UniqueEmail("email-disable"));
        var twoFactorAuth = await SeedActiveAsync(person, appEnabled: false, emailEnabled: true);
        await SeedEmailCodeAsync(twoFactorAuth.Id, EmailCode);
        Authorize(TestTokens.For(person.PublicId, (int)Roles.SystemAdmin));

        // When
        var response = await DisableAsync(code: EmailCode);

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Body!.Data!.Disabled);
        Assert.Null(await ConfigurationForAsync(person));
    }

    [FunctionalFact]
    public async Task GivenValidPasswordAndRecoveryCode_WhenPostDisable_ThenTwoFactorAuthAndRecoveryCodesAreRemoved()
    {
        // Given an active configuration and one of the person's recovery codes
        var person = await SeedPersonAsync(Roles.SystemAdmin, UniqueEmail("recovery-disable"));
        var twoFactorAuth = await SeedActiveAsync(
            person, appEnabled: true, emailEnabled: false, totpSecretEncrypted: [1, 2, 3, 4]);
        var recoveryCode = await SeedRecoveryCodeAsync(twoFactorAuth.Id);
        await SeedRecoveryCodeAsync(twoFactorAuth.Id, used: true);
        Authorize(TestTokens.For(person.PublicId, (int)Roles.SystemAdmin));

        // When
        var response = await DisableAsync(recoveryCode: recoveryCode);

        // Then — response
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Body!.Data!.Disabled);

        // Then — database state: the configuration and every one of its recovery codes are gone,
        // including the already-used one
        Assert.Null(await ConfigurationForAsync(person));
        Assert.Equal(0, await RecoveryCodeCountForAsync(twoFactorAuth.Id));
    }

    [FunctionalFact]
    public async Task GivenTwoFactorNeverEnabled_WhenPostDisable_ThenNotFound()
    {
        // Given — AF-39a: no TWO_FACTOR_AUTH row at all
        var person = await SeedPersonAsync(Roles.SystemAdmin, UniqueEmail("never-enabled"));
        Authorize(TestTokens.For(person.PublicId, (int)Roles.SystemAdmin));

        // When
        var response = await DisableAsync(code: "123456");

        // Then
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains(TwoFactorMessages.NotActive, response.Body!.Errors);
    }

    [FunctionalFact]
    public async Task GivenPendingNotYetActiveConfiguration_WhenPostDisable_ThenNotFound()
    {
        // Given — AF-39a: a row exists (UC-36 initiated it) but was never confirmed by UC-37
        var person = await SeedPersonAsync(Roles.SystemAdmin, UniqueEmail("pending-only"));
        await using (var context = db.CreateContext())
        {
            context.TwoFactorAuths.Add(new TwoFactorAuth
            {
                PersonId = person.Id, IsActive = false, AppEnabled = true, TotpSecretEncrypted = [1, 2, 3]
            });
            await context.SaveChangesAsync();
        }
        Authorize(TestTokens.For(person.PublicId, (int)Roles.SystemAdmin));

        // When
        var response = await DisableAsync(code: "123456");

        // Then
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains(TwoFactorMessages.NotActive, response.Body!.Errors);
    }

    [FunctionalFact]
    public async Task GivenWrongPassword_WhenPostDisable_ThenUnauthorizedAndRowSurvives()
    {
        // Given — AF-39b
        var person = await SeedPersonAsync(Roles.SystemAdmin, UniqueEmail("wrong-password"));
        await SeedActiveAsync(person, appEnabled: true, emailEnabled: false, totpSecretEncrypted: [1, 2, 3, 4]);
        Authorize(TestTokens.For(person.PublicId, (int)Roles.SystemAdmin));

        // When
        var response = await DisableAsync(password: "wrong-password", code: "000000");

        // Then
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(TwoFactorMessages.PasswordMismatch, response.Body!.Errors);
        Assert.NotNull(await ConfigurationForAsync(person));
    }

    [FunctionalFact]
    public async Task GivenCorrectPasswordAndWrongCode_WhenPostDisable_ThenUnauthorizedAndRowSurvives()
    {
        // Given — AF-39c
        var person = await SeedPersonAsync(Roles.SystemAdmin, UniqueEmail("wrong-factor"));
        await SeedActiveAsync(person, appEnabled: true, emailEnabled: false, totpSecretEncrypted: [1, 2, 3, 4]);
        Authorize(TestTokens.For(person.PublicId, (int)Roles.SystemAdmin));

        // When
        var response = await DisableAsync(code: "000000");

        // Then
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(TwoFactorMessages.FactorInvalid, response.Body!.Errors);
        Assert.NotNull(await ConfigurationForAsync(person));
    }

    [FunctionalFact]
    public async Task GivenCorrectPasswordAndAlreadyUsedRecoveryCode_WhenPostDisable_ThenUnauthorized()
    {
        // Given — AF-39c's recovery-code shape
        var person = await SeedPersonAsync(Roles.SystemAdmin, UniqueEmail("used-recovery-disable"));
        var twoFactorAuth = await SeedActiveAsync(
            person, appEnabled: true, emailEnabled: false, totpSecretEncrypted: [1, 2, 3, 4]);
        var recoveryCode = await SeedRecoveryCodeAsync(twoFactorAuth.Id, used: true);
        Authorize(TestTokens.For(person.PublicId, (int)Roles.SystemAdmin));

        // When
        var response = await DisableAsync(recoveryCode: recoveryCode);

        // Then
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(TwoFactorMessages.FactorInvalid, response.Body!.Errors);
        Assert.NotNull(await ConfigurationForAsync(person));
    }

    [FunctionalFact]
    public async Task GivenNoBearerToken_WhenPostDisable_ThenUnauthorized()
    {
        // Given no bearer token on the gateway
        // When
        var response = await DisableAsync(code: "123456");

        // Then
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
