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
using ArturRios.Heimdall.WebApi.Tests.Support;
using ArturRios.Output;
using ArturRios.Util.Hashing;
using ArturRios.Util.Http;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Functional;
using Microsoft.EntityFrameworkCore;
using OtpNet;

namespace ArturRios.Heimdall.WebApi.Tests;

// Functional tests for POST /api/auth/2fa/verify (UC-38, FR-2F-06…FR-2F-09): a full
// enable -> confirm -> login (challenge) -> verify (token) round trip, the main flow via each
// method (app code, email code, recovery code), AF-38a (401, invalid/expired/non-challenge
// token), AF-38b/AF-38c (401, identical message for a wrong code and a reused recovery code), and
// a check that MfaPendingGuardFilter (FR-2F-10) rejects a challenge token used as a bearer
// credential anywhere else.
[Collection(nameof(FunctionalCollection))]
public class AuthControllerVerifyTwoFactorAuthTests(PostgresFixture db) : WebApiTest<Program>(EnvironmentType.Local)
{
    private const string Password = "Str0ng-2fa-Pass!";
    private const string EmailCode = "123456";

    private static string UniqueEmail(string prefix) => $"{prefix}-{Guid.NewGuid():N}@test.local";

    private static string CurrentTotpCode(string base32Secret) =>
        new Totp(Base32Encoding.ToBytes(base32Secret)).ComputeTotp();

    private async Task<Person> SeedPersonAsync(Roles role, string email, bool emailVerified = true)
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
            EmailVerified = emailVerified
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

    private Task<HttpOutput<DataOutput<LoginCommandOutput?>?>> LoginAsync(string email) =>
        Gateway.PostAsync<DataOutput<LoginCommandOutput?>>(
            "/api/auth/login", new LoginCommand { Email = email, Password = Password });

    private Task<HttpOutput<DataOutput<EnableTwoFactorAuthCommandOutput?>?>> EnableAsync(
        params string[] methods) =>
        Gateway.PostAsync<DataOutput<EnableTwoFactorAuthCommandOutput?>>(
            "/api/auth/2fa/enable", new EnableTwoFactorAuthCommand { Methods = methods.ToList() });

    private Task<HttpOutput<DataOutput<ConfirmTwoFactorAuthCommandOutput?>?>> ConfirmAsync(
        string? appCode = null, string? emailCode = null) =>
        Gateway.PostAsync<DataOutput<ConfirmTwoFactorAuthCommandOutput?>>(
            "/api/auth/2fa/confirm", new ConfirmTwoFactorAuthCommand { AppCode = appCode, EmailCode = emailCode });

    private Task<HttpOutput<DataOutput<VerifyTwoFactorAuthCommandOutput?>?>> VerifyAsync(
        string challengeToken, string? code = null, string? recoveryCode = null) =>
        Gateway.PostAsync<DataOutput<VerifyTwoFactorAuthCommandOutput?>>(
            "/api/auth/2fa/verify",
            new VerifyTwoFactorAuthCommand
            {
                ChallengeToken = challengeToken, Code = code, RecoveryCode = recoveryCode
            });

    /// <summary>Same helper <c>AuthControllerConfirmTwoFactorAuthTests</c> uses: initiates App setup
    /// for real so the pending row's secret is genuinely Data-Protection-encrypted.</summary>
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
    public async Task GivenAppCodeAlreadyRedeemed_WhenPostTwoFactorVerifyAgain_ThenUnauthorized()
    {
        // Given a person who has enabled and confirmed App-based 2FA, and has already completed one
        // 2FA-gated login with the current app code
        var person = await SeedPersonAsync(Roles.SystemAdmin, UniqueEmail("totp-replay"));
        Authorize(TestTokens.For(person.PublicId, (int)Roles.SystemAdmin));
        var secret = await EnableAppAsync("App");
        Assert.Equal(HttpStatusCode.OK, (await ConfirmAsync(CurrentTotpCode(secret))).StatusCode);

        var appCode = CurrentTotpCode(secret);

        await db.ForgetLastTotpStepAsync(person.PublicId);

        var firstLogin = await LoginAsync(person.Email);
        var firstVerify = await VerifyAsync(firstLogin.Body!.Data!.ChallengeToken!, appCode);
        Assert.Equal(HttpStatusCode.OK, firstVerify.StatusCode);

        // When an attacker who observed that same code — over a shoulder, through a phishing proxy,
        // or in a logged request body — presents it again inside the same 30-second step, holding a
        // challenge token of their own from the password they already knew
        var secondLogin = await LoginAsync(person.Email);
        var replay = await VerifyAsync(secondLogin.Body!.Data!.ChallengeToken!, appCode);

        // Then it is refused: an app code is good exactly once (FR-2F-09, RFC 6238 §5.2), and the
        // refusal is AF-38b's ordinary message, which says nothing about why
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        Assert.Null(replay.Body?.Data?.Token);
        Assert.Contains(TwoFactorMessages.FactorInvalid, replay.Body!.Errors);
    }

    [FunctionalFact]
    public async Task GivenFullTwoFactorFlow_WhenEnablingConfirmingLoggingInAndVerifying_ThenFullTokenIsIssued()
    {
        // Given a person who enables and confirms App-based 2FA through the real endpoints
        var person = await SeedPersonAsync(Roles.SystemAdmin, UniqueEmail("full-flow"));
        Authorize(TestTokens.For(person.PublicId, (int)Roles.SystemAdmin));
        var secret = await EnableAppAsync("App");
        var confirm = await ConfirmAsync(CurrentTotpCode(secret));
        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);

        // The confirmation spent the current time step, and an app code is good once. Redeeming the
        // challenge below with a code from that same step is the replay the guard refuses — covered
        // on its own by GivenAppCodeAlreadyUsed_..., so forget the step here rather than sleep it
        // out. See PostgresFixture.ForgetLastTotpStepAsync.
        await db.ForgetLastTotpStepAsync(person.PublicId);

        // When logging in with the correct password. The stale bearer header from the setup above is
        // simply ignored: /api/auth/login is [AllowAnonymous], so AuthenticationMiddleware never
        // looks at it.
        var login = await LoginAsync(person.Email);

        // Then — a challenge token, not a full one (AF-11g)
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.True(login.Body!.Data!.RequiresTwoFactor);
        Assert.NotNull(login.Body.Data.ChallengeToken);
        Assert.Null(login.Body.Data.Token);
        Assert.Equal(["App"], login.Body.Data.AvailableMethods);
        Assert.Contains(AuthMessages.TwoFactorRequired, login.Body.Messages);

        // When redeeming the challenge token with the current app code
        var verify = await VerifyAsync(login.Body.Data.ChallengeToken!, CurrentTotpCode(secret));

        // Then — a full authentication token, usable exactly like a direct login's
        Assert.Equal(HttpStatusCode.OK, verify.StatusCode);
        Assert.NotNull(verify.Body?.Data?.Token);
        Assert.True(verify.Body!.Data!.ExpiresAt > DateTime.UtcNow);
        Assert.Contains(TwoFactorMessages.VerificationSuccessful, verify.Body.Messages);

        // The person seeded above is verified, so this is where the response says so (FR-EV-05) —
        // the positive counterpart of GivenUnverifiedGatedPerson_...
        Assert.True(verify.Body.Data.EmailVerified);

        // Replaces the stale bearer header from the setup step above — Authorize only adds a header,
        // it does not overwrite one already present.
        Gateway.Client.DefaultRequestHeaders.Remove("Authorization");
        Authorize(verify.Body!.Data!.Token);
        var whoAmI = await Gateway.GetAsync<DataOutput<PersonOutput?>>($"/api/persons/{person.PublicId}");
        Assert.Equal(HttpStatusCode.OK, whoAmI.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenUnverifiedGatedPerson_WhenPostTwoFactorVerify_ThenResponseReportsEmailVerifiedFalse()
    {
        // Given a 2FA-gated person whose address is unverified: login gave them a challenge that
        // deliberately said nothing about the account, so this is where they learn it (FR-EV-05)
        var person = await SeedPersonAsync(
            Roles.SystemAdmin, UniqueEmail("gated-unverified"), emailVerified: false);
        Authorize(TestTokens.For(person.PublicId, (int)Roles.SystemAdmin));
        var secret = await EnableAppAsync("App");
        var confirm = await ConfirmAsync(CurrentTotpCode(secret));
        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);

        // The confirmation spent the current time step, and an app code is good once. Redeeming the
        // challenge below with a code from that same step is the replay the guard refuses — covered
        // on its own by GivenAppCodeAlreadyUsed_..., so forget the step here rather than sleep it
        // out. See PostgresFixture.ForgetLastTotpStepAsync.
        await db.ForgetLastTotpStepAsync(person.PublicId);

        var login = await LoginAsync(person.Email);
        Assert.True(login.Body!.Data!.RequiresTwoFactor);

        // When redeeming the challenge token with the current app code
        var verify = await VerifyAsync(login.Body.Data.ChallengeToken!, CurrentTotpCode(secret));

        // Then — the full token, and the verification status the challenge withheld
        Assert.Equal(HttpStatusCode.OK, verify.StatusCode);
        Assert.NotNull(verify.Body?.Data?.Token);
        Assert.False(verify.Body!.Data!.EmailVerified);
    }

    [FunctionalFact]
    public async Task GivenValidEmailCode_WhenPostVerify_ThenFullTokenIsIssued()
    {
        // Given an active email-only configuration and a challenge token for that person
        var person = await SeedPersonAsync(Roles.SystemAdmin, UniqueEmail("email-2fa"));
        var twoFactorAuth = await SeedActiveAsync(person, appEnabled: false, emailEnabled: true);
        await SeedEmailCodeAsync(twoFactorAuth.Id, EmailCode);
        var challengeToken = TestTokens.ForMfaPending(person.PublicId, (int)Roles.SystemAdmin);

        // When
        var response = await VerifyAsync(challengeToken, EmailCode);

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(response.Body?.Data?.Token);
    }

    [FunctionalFact]
    public async Task GivenValidRecoveryCode_WhenPostVerify_ThenFullTokenIsIssuedAndCodeIsMarkedUsed()
    {
        // Given an active configuration and one of the person's recovery codes
        var person = await SeedPersonAsync(Roles.SystemAdmin, UniqueEmail("recovery"));
        var twoFactorAuth = await SeedActiveAsync(person, appEnabled: true, emailEnabled: false);
        var recoveryCode = await SeedRecoveryCodeAsync(twoFactorAuth.Id);
        var challengeToken = TestTokens.ForMfaPending(person.PublicId, (int)Roles.SystemAdmin);

        // When
        var response = await VerifyAsync(challengeToken, recoveryCode: recoveryCode);

        // Then — response
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(response.Body?.Data?.Token);

        // Then — the recovery code cannot be used again (AF-38c)
        await using var context = db.CreateContext();
        var stored = await context.TwoFactorRecoveryCodes.FirstAsync(x => x.TwoFactorAuthId == twoFactorAuth.Id);
        Assert.True(stored.Used);
        Assert.NotNull(stored.UsedAt);
    }

    [FunctionalFact]
    public async Task GivenAlreadyUsedRecoveryCode_WhenPostVerify_ThenUnauthorizedWithSameMessageAsWrongCode()
    {
        // Given — AF-38c
        var person = await SeedPersonAsync(Roles.SystemAdmin, UniqueEmail("used-recovery"));
        var twoFactorAuth = await SeedActiveAsync(person, appEnabled: true, emailEnabled: false);
        var recoveryCode = await SeedRecoveryCodeAsync(twoFactorAuth.Id, used: true);
        var challengeToken = TestTokens.ForMfaPending(person.PublicId, (int)Roles.SystemAdmin);

        // When
        var response = await VerifyAsync(challengeToken, recoveryCode: recoveryCode);

        // Then — the same message and status a wrong code gets (AF-38b), never distinguished
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(TwoFactorMessages.FactorInvalid, response.Body!.Errors);
    }

    [FunctionalFact]
    public async Task GivenWrongAppCode_WhenPostVerify_ThenUnauthorized()
    {
        // Given — AF-38b
        var person = await SeedPersonAsync(Roles.SystemAdmin, UniqueEmail("wrong-code"));
        await SeedActiveAsync(person, appEnabled: true, emailEnabled: false, totpSecretEncrypted: [1, 2, 3, 4]);
        var challengeToken = TestTokens.ForMfaPending(person.PublicId, (int)Roles.SystemAdmin);

        // When
        var response = await VerifyAsync(challengeToken, "000000");

        // Then
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(TwoFactorMessages.FactorInvalid, response.Body!.Errors);
    }

    [FunctionalFact]
    public async Task GivenGarbageChallengeToken_WhenPostVerify_ThenUnauthorized()
    {
        // Given — AF-38a
        // When
        var response = await VerifyAsync("not-a-real-token", "123456");

        // Then
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(TwoFactorMessages.ChallengeTokenInvalid, response.Body!.Errors);
    }

    [FunctionalFact]
    public async Task GivenFullLoginTokenInsteadOfChallengeToken_WhenPostVerify_ThenUnauthorized()
    {
        // Given — AF-38a's other shape: a genuinely signed token that simply is not a challenge (no
        // MFA-pending claim) must not be accepted here either (FR-2F-10)
        var normalToken = TestTokens.For(Guid.NewGuid(), (int)Roles.SystemAdmin);

        // When
        var response = await VerifyAsync(normalToken, "123456");

        // Then
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(TwoFactorMessages.ChallengeTokenInvalid, response.Body!.Errors);
    }

    [FunctionalFact]
    public async Task GivenChallengeTokenUsedAsBearerHeader_WhenCallingAnotherEndpoint_ThenUnauthorized()
    {
        // Given a UC-38 challenge token — MfaPendingGuardFilter's target (FR-2F-10)
        var challengeToken = TestTokens.ForMfaPending(Guid.NewGuid(), (int)Roles.SystemAdmin);
        Authorize(challengeToken);

        // When it is misused as a bearer credential against an unrelated authenticated endpoint
        var response = await EnableAsync("App");

        // Then — rejected, even though the token is genuinely signed and would otherwise resolve to
        // an authenticated identity
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenNoBearerTokenAtAll_WhenPostLogin_ThenGuardFilterDoesNotBlockTheAnonymousRequest()
    {
        // Given no Authorization header whatsoever (this test method never calls Authorize) —
        // MfaPendingGuardFilter must be a no-op here, not an accidental authentication requirement of
        // its own
        var person = await SeedPersonAsync(Roles.SystemAdmin, UniqueEmail("anon-ok"));

        // When
        var response = await LoginAsync(person.Email);

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
