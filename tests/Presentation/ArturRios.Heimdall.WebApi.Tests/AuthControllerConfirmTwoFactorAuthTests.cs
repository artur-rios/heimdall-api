using System.Net;
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

// Functional tests for POST /api/auth/2fa/confirm (UC-37, FR-2F-04/05): the main flow for each
// method combination, AF-37a (404, no pending setup, including a token naming no eligible person),
// AF-37b (400, appCode missing/incorrect), AF-37c (400, emailCode missing/incorrect/expired/used),
// AF-37d (409, already active), and the 401 an unauthenticated caller gets.
[Collection(nameof(FunctionalCollection))]
public class AuthControllerConfirmTwoFactorAuthTests(PostgresFixture db)
    : WebApiTest<Program>(EnvironmentType.Local)
{
    private const string Password = "Str0ng-2fa-Pass!";
    private const string Base32Secret = "JBSWY3DPEHPK3PXP";
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

    private async Task<TwoFactorAuth> SeedPendingAsync(Person person, bool appEnabled, bool emailEnabled)
    {
        await using var context = db.CreateContext();
        var twoFactorAuth = new TwoFactorAuth
        {
            PersonId = person.Id,
            IsActive = false,
            AppEnabled = appEnabled,
            EmailEnabled = emailEnabled,
            TotpSecretEncrypted = appEnabled ? [1, 2, 3] : null
        };
        context.TwoFactorAuths.Add(twoFactorAuth);
        await context.SaveChangesAsync();
        return twoFactorAuth;
    }

    private async Task<TwoFactorEmailCode> SeedEmailCodeAsync(
        long twoFactorAuthId, bool used = false, bool expired = false)
    {
        await using var context = db.CreateContext();
        var codeHash = Hash.EncodeWithRandomSalt(EmailCode, out var salt);
        var emailCode = new TwoFactorEmailCode
        {
            TwoFactorAuthId = twoFactorAuthId,
            CodeHash = codeHash,
            Salt = salt,
            ExpiresAt = expired ? DateTime.UtcNow.AddMinutes(-1) : DateTime.UtcNow.AddMinutes(10),
            Used = used
        };
        context.TwoFactorEmailCodes.Add(emailCode);
        await context.SaveChangesAsync();
        return emailCode;
    }

    private async Task<TwoFactorAuth?> ConfigurationForAsync(Person person)
    {
        await using var context = db.CreateContext();

        return await context.TwoFactorAuths
            .Include(x => x.RecoveryCodes)
            .FirstOrDefaultAsync(x => x.PersonId == person.Id);
    }

    private Task<HttpOutput<DataOutput<ConfirmTwoFactorAuthCommandOutput?>?>> ConfirmAsync(
        string? appCode = null, string? emailCode = null) =>
        Gateway.PostAsync<DataOutput<ConfirmTwoFactorAuthCommandOutput?>>(
            "/api/auth/2fa/confirm", new ConfirmTwoFactorAuthCommand { AppCode = appCode, EmailCode = emailCode });

    private Task<HttpOutput<DataOutput<EnableTwoFactorAuthCommandOutput?>?>> EnableAsync(
        params string[] methods) =>
        Gateway.PostAsync<DataOutput<EnableTwoFactorAuthCommandOutput?>>(
            "/api/auth/2fa/enable", new EnableTwoFactorAuthCommand { Methods = methods.ToList() });

    /// <summary>
    ///     Initiates App setup through the real <c>/2fa/enable</c> endpoint (as opposed to
    ///     <see cref="SeedPendingAsync" />'s direct insert) so the pending row's
    ///     <c>TotpSecretEncrypted</c> is genuinely Data-Protection-encrypted — required by every test
    ///     here that submits a non-empty <c>appCode</c>, since <c>ConfirmTwoFactorAuthCommandHandler</c>
    ///     unprotects it for real, unlike the unit tests' mocked protector.
    /// </summary>
    private async Task<string> EnableAppAsync(params string[] methods)
    {
        var response = await EnableAsync(methods);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var uri = response.Body!.Data!.OtpAuthUri!;
        var afterSecret = uri[(uri.IndexOf("secret=", StringComparison.Ordinal) + "secret=".Length)..];
        var ampersandIndex = afterSecret.IndexOf('&');

        return ampersandIndex < 0 ? afterSecret : afterSecret[..ampersandIndex];
    }

    [FunctionalFact]
    public async Task GivenValidAppAndEmailCodes_WhenPostConfirm2fa_ThenSetupIsActivatedWithTenRecoveryCodes()
    {
        // Given a pending setup for both methods, enabled for real so the TOTP secret is genuinely
        // encrypted
        var person = await SeedPersonAsync(Roles.SystemAdmin, UniqueEmail("admin"));
        Authorize(TestTokens.For(person.PublicId, (int)Roles.SystemAdmin));
        var secret = await EnableAppAsync("App", "Email");
        var pending = (await ConfigurationForAsync(person))!;
        await SeedEmailCodeAsync(pending.Id);

        // When
        var response = await ConfirmAsync(CurrentTotpCode(secret), EmailCode);

        // Then — response
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(TwoFactorMessages.SetupConfirmed, response.Body!.Messages);
        Assert.True(response.Body.Data!.Enabled);
        Assert.Equal(10, response.Body.Data.RecoveryCodes.Count);
        Assert.Equal(10, response.Body.Data.RecoveryCodes.Distinct().Count());

        // Then — database state
        var stored = await ConfigurationForAsync(person);
        Assert.NotNull(stored);
        Assert.True(stored!.IsActive);
        Assert.Equal(10, stored.RecoveryCodes.Count);
        Assert.All(stored.RecoveryCodes, code => Assert.False(code.Used));
    }

    [FunctionalFact]
    public async Task GivenOnlyAppEnabledAndValidAppCode_WhenPostConfirm2fa_ThenSetupIsActivated()
    {
        // Given, enabled for real so the TOTP secret is genuinely encrypted
        var person = await SeedPersonAsync(Roles.User, UniqueEmail("user"));
        Authorize(TestTokens.For(person.PublicId, (int)Roles.User));
        var secret = await EnableAppAsync("App");

        // When
        var response = await ConfirmAsync(CurrentTotpCode(secret));

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = await ConfigurationForAsync(person);
        Assert.True(stored!.IsActive);
    }

    [FunctionalFact]
    public async Task GivenOnlyEmailEnabledAndValidEmailCode_WhenPostConfirm2fa_ThenSetupIsActivated()
    {
        // Given
        var person = await SeedPersonAsync(Roles.ScopeAdmin, UniqueEmail("scope-admin"));
        Authorize(TestTokens.For(person.PublicId, (int)Roles.ScopeAdmin));
        var pending = await SeedPendingAsync(person, appEnabled: false, emailEnabled: true);
        await SeedEmailCodeAsync(pending.Id);

        // When
        var response = await ConfirmAsync(emailCode: EmailCode);

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = await ConfigurationForAsync(person);
        Assert.True(stored!.IsActive);
    }

    [FunctionalFact]
    public async Task GivenNoPendingSetup_WhenPostConfirm2fa_ThenNotFound()
    {
        // Given — AF-37a
        var person = await SeedPersonAsync(Roles.SystemAdmin, UniqueEmail("admin"));
        Authorize(TestTokens.For(person.PublicId, (int)Roles.SystemAdmin));

        // When
        var response = await ConfirmAsync(CurrentTotpCode(Base32Secret));

        // Then
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains(TwoFactorMessages.NoPendingSetup, response.Body!.Errors);
    }

    [FunctionalFact]
    public async Task GivenTokenNamingNoEligiblePerson_WhenPostConfirm2fa_ThenNotFound()
    {
        // Given — AF-37a's other shape: no live Person named by the token, the same shape a
        // Google-issued token has here (UC-25 step 8).
        Authorize(TestTokens.ForRole((int)Roles.User));

        // When
        var response = await ConfirmAsync(CurrentTotpCode(Base32Secret));

        // Then
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains(TwoFactorMessages.NoPendingSetup, response.Body!.Errors);
    }

    [FunctionalFact]
    public async Task GivenAppEnabledAndMissingAppCode_WhenPostConfirm2fa_ThenBadRequest()
    {
        // Given — AF-37b
        var person = await SeedPersonAsync(Roles.SystemAdmin, UniqueEmail("admin"));
        Authorize(TestTokens.For(person.PublicId, (int)Roles.SystemAdmin));
        await SeedPendingAsync(person, appEnabled: true, emailEnabled: false);

        // When
        var response = await ConfirmAsync();

        // Then
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(TwoFactorMessages.AppCodeInvalid, response.Body!.Errors);
        var stored = await ConfigurationForAsync(person);
        Assert.False(stored!.IsActive);
    }

    [FunctionalFact]
    public async Task GivenAppEnabledAndIncorrectAppCode_WhenPostConfirm2fa_ThenBadRequest()
    {
        // Given — AF-37b, enabled for real so the TOTP secret is genuinely encrypted
        var person = await SeedPersonAsync(Roles.SystemAdmin, UniqueEmail("admin"));
        Authorize(TestTokens.For(person.PublicId, (int)Roles.SystemAdmin));
        await EnableAppAsync("App");

        // When
        var response = await ConfirmAsync("000000");

        // Then
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(TwoFactorMessages.AppCodeInvalid, response.Body!.Errors);
    }

    [FunctionalFact]
    public async Task GivenEmailEnabledAndMissingEmailCode_WhenPostConfirm2fa_ThenBadRequest()
    {
        // Given — AF-37c
        var person = await SeedPersonAsync(Roles.SystemAdmin, UniqueEmail("admin"));
        Authorize(TestTokens.For(person.PublicId, (int)Roles.SystemAdmin));
        var pending = await SeedPendingAsync(person, appEnabled: false, emailEnabled: true);
        await SeedEmailCodeAsync(pending.Id);

        // When
        var response = await ConfirmAsync();

        // Then
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(TwoFactorMessages.EmailCodeInvalid, response.Body!.Errors);
        var stored = await ConfigurationForAsync(person);
        Assert.False(stored!.IsActive);
    }

    [FunctionalFact]
    public async Task GivenEmailEnabledAndIncorrectEmailCode_WhenPostConfirm2fa_ThenBadRequest()
    {
        // Given — AF-37c
        var person = await SeedPersonAsync(Roles.SystemAdmin, UniqueEmail("admin"));
        Authorize(TestTokens.For(person.PublicId, (int)Roles.SystemAdmin));
        var pending = await SeedPendingAsync(person, appEnabled: false, emailEnabled: true);
        await SeedEmailCodeAsync(pending.Id);

        // When
        var response = await ConfirmAsync(emailCode: "654321");

        // Then
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(TwoFactorMessages.EmailCodeInvalid, response.Body!.Errors);
    }

    [FunctionalFact]
    public async Task GivenEmailEnabledAndExpiredEmailCode_WhenPostConfirm2fa_ThenBadRequest()
    {
        // Given — AF-37c
        var person = await SeedPersonAsync(Roles.SystemAdmin, UniqueEmail("admin"));
        Authorize(TestTokens.For(person.PublicId, (int)Roles.SystemAdmin));
        var pending = await SeedPendingAsync(person, appEnabled: false, emailEnabled: true);
        await SeedEmailCodeAsync(pending.Id, expired: true);

        // When
        var response = await ConfirmAsync(emailCode: EmailCode);

        // Then
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(TwoFactorMessages.EmailCodeInvalid, response.Body!.Errors);
    }

    [FunctionalFact]
    public async Task GivenEmailEnabledAndAlreadyUsedEmailCode_WhenPostConfirm2fa_ThenBadRequest()
    {
        // Given — AF-37c
        var person = await SeedPersonAsync(Roles.SystemAdmin, UniqueEmail("admin"));
        Authorize(TestTokens.For(person.PublicId, (int)Roles.SystemAdmin));
        var pending = await SeedPendingAsync(person, appEnabled: false, emailEnabled: true);
        await SeedEmailCodeAsync(pending.Id, used: true);

        // When
        var response = await ConfirmAsync(emailCode: EmailCode);

        // Then
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(TwoFactorMessages.EmailCodeInvalid, response.Body!.Errors);
    }

    [FunctionalFact]
    public async Task GivenAlreadyActiveConfiguration_WhenPostConfirm2fa_ThenConflict()
    {
        // Given — AF-37d
        var person = await SeedPersonAsync(Roles.SystemAdmin, UniqueEmail("admin"));
        Authorize(TestTokens.For(person.PublicId, (int)Roles.SystemAdmin));
        await using (var context = db.CreateContext())
        {
            context.TwoFactorAuths.Add(new TwoFactorAuth
            {
                PersonId = person.Id, IsActive = true, AppEnabled = true
            });
            await context.SaveChangesAsync();
        }

        // When
        var response = await ConfirmAsync(CurrentTotpCode(Base32Secret));

        // Then
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains(TwoFactorMessages.AlreadyActive, response.Body!.Errors);
    }

    [FunctionalFact]
    public async Task GivenNoBearerToken_WhenPostConfirm2fa_ThenUnauthorized()
    {
        // Given no bearer token on the gateway
        // When
        var response = await ConfirmAsync(CurrentTotpCode(Base32Secret));

        // Then
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
