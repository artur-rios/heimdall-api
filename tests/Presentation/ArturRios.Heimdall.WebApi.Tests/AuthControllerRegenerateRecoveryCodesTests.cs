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

// Functional tests for POST /api/auth/2fa/recovery-codes/regenerate (UC-40, FR-2F-12): the main flow
// via an app code, an email code, and a recovery code, each confirming the previous ten recovery
// codes are gone from the database and exactly ten new hashed rows exist; AF-40a (404, not active);
// AF-40b (401, invalid second factor); and the 401 an unauthenticated caller gets.
[Collection(nameof(FunctionalCollection))]
public class AuthControllerRegenerateRecoveryCodesTests(PostgresFixture db) : WebApiTest<Program>(EnvironmentType.Local)
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

    private async Task<List<TwoFactorRecoveryCode>> RecoveryCodesForAsync(long twoFactorAuthId)
    {
        await using var context = db.CreateContext();

        return await context.TwoFactorRecoveryCodes
            .Where(x => x.TwoFactorAuthId == twoFactorAuthId)
            .ToListAsync();
    }

    private Task<HttpOutput<DataOutput<RegenerateRecoveryCodesCommandOutput?>?>> RegenerateAsync(
        string? code = null, string? recoveryCode = null) =>
        Gateway.PostAsync<DataOutput<RegenerateRecoveryCodesCommandOutput?>>(
            "/api/auth/2fa/recovery-codes/regenerate",
            new RegenerateRecoveryCodesCommand { Code = code, RecoveryCode = recoveryCode });

    /// <summary>
    ///     Replaces the pending row's <c>TotpSecretEncrypted</c> with one genuinely produced by the
    ///     real <c>/2fa/enable</c> + <c>/2fa/confirm</c> endpoints, then reactivates it as an App-only
    ///     configuration — needed because <c>RegenerateRecoveryCodesCommandHandler</c> unprotects the
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

        return secret;
    }

    [FunctionalFact]
    public async Task GivenValidAppCode_WhenPostRegenerate_ThenOldRecoveryCodesAreReplacedWithTenNewOnes()
    {
        // Given an App-only configuration enabled and confirmed for real, plus an extra pre-existing
        // recovery code seeded directly (on top of the ten UC-37's confirm already issued)
        var person = await SeedPersonAsync(Roles.SystemAdmin, UniqueEmail("app-regenerate"));
        Authorize(TestTokens.For(person.PublicId, (int)Roles.SystemAdmin));
        var secret = await EnableAppSecretAsync(person);
        var twoFactorAuth = (await ConfigurationForAsync(person))!;
        var oldCodes = await RecoveryCodesForAsync(twoFactorAuth.Id);
        Assert.Equal(10, oldCodes.Count);

        // When
        var response = await RegenerateAsync(code: CurrentTotpCode(secret));

        // Then — response
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(TwoFactorMessages.RecoveryCodesRegenerated, response.Body!.Messages);
        Assert.Equal(10, response.Body.Data!.RecoveryCodes.Count);
        Assert.Equal(10, response.Body.Data.RecoveryCodes.Distinct().Count());

        // Then — database state: exactly ten rows exist, none matching the old ones' hashes
        var newCodes = await RecoveryCodesForAsync(twoFactorAuth.Id);
        Assert.Equal(10, newCodes.Count);
        var oldHashes = oldCodes.Select(c => c.CodeHash).ToList();
        Assert.DoesNotContain(newCodes, nc => oldHashes.Any(oh => oh.SequenceEqual(nc.CodeHash)));
        Assert.All(newCodes, nc => Assert.False(nc.Used));
    }

    [FunctionalFact]
    public async Task GivenValidEmailCode_WhenPostRegenerate_ThenTenNewRecoveryCodesAreIssued()
    {
        // Given an active email-only configuration, a live email code, and one pre-existing recovery code
        var person = await SeedPersonAsync(Roles.SystemAdmin, UniqueEmail("email-regenerate"));
        var twoFactorAuth = await SeedActiveAsync(person, appEnabled: false, emailEnabled: true);
        await SeedEmailCodeAsync(twoFactorAuth.Id, EmailCode);
        await SeedRecoveryCodeAsync(twoFactorAuth.Id);
        Authorize(TestTokens.For(person.PublicId, (int)Roles.SystemAdmin));

        // When
        var response = await RegenerateAsync(code: EmailCode);

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(10, response.Body!.Data!.RecoveryCodes.Count);
        Assert.Equal(10, (await RecoveryCodesForAsync(twoFactorAuth.Id)).Count);
    }

    [FunctionalFact]
    public async Task GivenValidRecoveryCode_WhenPostRegenerate_ThenOldRecoveryCodesNoLongerValidate()
    {
        // Given an active configuration and one of the person's recovery codes
        var person = await SeedPersonAsync(Roles.SystemAdmin, UniqueEmail("recovery-regenerate"));
        var twoFactorAuth = await SeedActiveAsync(
            person, appEnabled: true, emailEnabled: false, totpSecretEncrypted: [1, 2, 3, 4]);
        var recoveryCode = await SeedRecoveryCodeAsync(twoFactorAuth.Id);
        await SeedRecoveryCodeAsync(twoFactorAuth.Id, used: true);
        Authorize(TestTokens.For(person.PublicId, (int)Roles.SystemAdmin));

        // When
        var response = await RegenerateAsync(recoveryCode: recoveryCode);

        // Then — response
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(10, response.Body!.Data!.RecoveryCodes.Count);

        var newCodes = await RecoveryCodesForAsync(twoFactorAuth.Id);
        Assert.Equal(10, newCodes.Count);

        // Then — the code just used to authorize regeneration is now rejected, both by this endpoint
        // again and by /2fa/verify's equivalent check
        var replay = await RegenerateAsync(recoveryCode: recoveryCode);
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        Assert.Contains(TwoFactorMessages.FactorInvalid, replay.Body!.Errors);
    }

    [FunctionalFact]
    public async Task GivenTwoFactorNeverEnabled_WhenPostRegenerate_ThenNotFound()
    {
        // Given — AF-40a: no TWO_FACTOR_AUTH row at all
        var person = await SeedPersonAsync(Roles.SystemAdmin, UniqueEmail("never-enabled-regen"));
        Authorize(TestTokens.For(person.PublicId, (int)Roles.SystemAdmin));

        // When
        var response = await RegenerateAsync(code: "123456");

        // Then
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains(TwoFactorMessages.NotActive, response.Body!.Errors);
    }

    [FunctionalFact]
    public async Task GivenPendingNotYetActiveConfiguration_WhenPostRegenerate_ThenNotFound()
    {
        // Given — AF-40a: a row exists (UC-36 initiated it) but was never confirmed by UC-37
        var person = await SeedPersonAsync(Roles.SystemAdmin, UniqueEmail("pending-only-regen"));
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
        var response = await RegenerateAsync(code: "123456");

        // Then
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains(TwoFactorMessages.NotActive, response.Body!.Errors);
    }

    [FunctionalFact]
    public async Task GivenWrongCode_WhenPostRegenerate_ThenUnauthorizedAndOldCodesSurvive()
    {
        // Given — AF-40b
        var person = await SeedPersonAsync(Roles.SystemAdmin, UniqueEmail("wrong-factor-regen"));
        var twoFactorAuth = await SeedActiveAsync(
            person, appEnabled: true, emailEnabled: false, totpSecretEncrypted: [1, 2, 3, 4]);
        await SeedRecoveryCodeAsync(twoFactorAuth.Id);
        Authorize(TestTokens.For(person.PublicId, (int)Roles.SystemAdmin));

        // When
        var response = await RegenerateAsync(code: "000000");

        // Then
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(TwoFactorMessages.FactorInvalid, response.Body!.Errors);
        Assert.Single(await RecoveryCodesForAsync(twoFactorAuth.Id));
    }

    [FunctionalFact]
    public async Task GivenAlreadyUsedRecoveryCode_WhenPostRegenerate_ThenUnauthorized()
    {
        // Given — AF-40b's recovery-code shape
        var person = await SeedPersonAsync(Roles.SystemAdmin, UniqueEmail("used-recovery-regen"));
        var twoFactorAuth = await SeedActiveAsync(
            person, appEnabled: true, emailEnabled: false, totpSecretEncrypted: [1, 2, 3, 4]);
        var recoveryCode = await SeedRecoveryCodeAsync(twoFactorAuth.Id, used: true);
        Authorize(TestTokens.For(person.PublicId, (int)Roles.SystemAdmin));

        // When
        var response = await RegenerateAsync(recoveryCode: recoveryCode);

        // Then
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(TwoFactorMessages.FactorInvalid, response.Body!.Errors);
        Assert.Single(await RecoveryCodesForAsync(twoFactorAuth.Id));
    }

    [FunctionalFact]
    public async Task GivenNoBearerToken_WhenPostRegenerate_ThenUnauthorized()
    {
        // Given no bearer token on the gateway
        // When
        var response = await RegenerateAsync(code: "123456");

        // Then
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
