using System.Net;
using System.Security.Cryptography;
using System.Text;
using ArturRios.Configuration.Enums;
using ArturRios.Heimdall.Command.Handlers;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Command.Services;
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

// Functional tests for what single-use and bounded means when requests arrive together: each test
// fires the same request in parallel against PostgreSQL and asserts the outcome is exactly-once, or
// exactly-the-cap, rather than "once per request that read the row before anyone wrote it".
//
// Before IAtomicWrites every one of these was read, check in memory, save: the requests all read the
// row first, all passed the check, and all acted — one recovery code or token redeemed by several, one
// challenge traded for several full tokens, a burst of wrong passwords counted as one and never locking
// the account. Each condition now lives in the WHERE clause of the write that spends it.
//
// The parallel batches stay at or under ten, the per-address rate limit on these endpoints, so what is
// asserted is the application's guarantee rather than the limiter's.
[Collection(nameof(FunctionalCollection))]
public class ConcurrentSingleUseTests(PostgresFixture db) : WebApiTest<Program>(EnvironmentType.Local)
{
    private const string Password = "Str0ng-Concurrency-Pass!";
    private const string EmailCode = "123456";

    private static string UniqueEmail(string prefix) => $"{prefix}-{Guid.NewGuid():N}@test.local";

    /// <summary>
    ///     Starts <paramref name="count" /> copies of a request at the same moment and waits for all
    ///     of them — a gate rather than a loop, so none has finished before the last has begun.
    /// </summary>
    private static async Task<T[]> InParallelAsync<T>(int count, Func<int, Task<T>> request)
    {
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var requests = Enumerable.Range(0, count)
            .Select(index => Task.Run(async () =>
            {
                await start.Task;
                return await request(index);
            }))
            .ToArray();

        start.SetResult();

        return await Task.WhenAll(requests);
    }

    private async Task<Person> SeedPersonAsync(string prefix)
    {
        await using var context = db.CreateContext();
        var person = new Person
        {
            PublicId = Guid.NewGuid(),
            Name = prefix,
            Email = UniqueEmail(prefix),
            PasswordHash = Hash.EncodeWithRandomSalt(Password, out var salt),
            Salt = salt,
            RoleId = (long)Roles.SystemAdmin,
            EmailVerified = true
        };
        context.Persons.Add(person);
        await context.SaveChangesAsync();
        return person;
    }

    private async Task<TwoFactorAuth> SeedTwoFactorAsync(
        Person person, bool isActive = true, bool appEnabled = true, bool emailEnabled = false)
    {
        await using var context = db.CreateContext();
        var twoFactorAuth = new TwoFactorAuth
        {
            PersonId = person.Id,
            IsActive = isActive,
            AppEnabled = appEnabled,
            EmailEnabled = emailEnabled,
            // Not a real Data-Protection payload, so every app code is refused — what a wrong guess
            // looks like, without having to know which codes are wrong at this instant.
            TotpSecretEncrypted = appEnabled ? [1, 2, 3, 4] : null,
            ChallengeId = TestTokens.SeededChallengeId
        };
        context.TwoFactorAuths.Add(twoFactorAuth);
        await context.SaveChangesAsync();
        return twoFactorAuth;
    }

    private async Task<string> SeedRecoveryCodeAsync(long twoFactorAuthId)
    {
        var plaintext = $"CONC-{Guid.NewGuid():N}"[..9];

        await using var context = db.CreateContext();
        context.TwoFactorRecoveryCodes.Add(new TwoFactorRecoveryCode
        {
            TwoFactorAuthId = twoFactorAuthId,
            CodeHash = SHA256.HashData(Encoding.UTF8.GetBytes(plaintext))
        });
        await context.SaveChangesAsync();

        return plaintext;
    }

    private async Task<TwoFactorAuth> ReloadTwoFactorAsync(long id)
    {
        await using var context = db.CreateContext();
        return await context.TwoFactorAuths.SingleAsync(x => x.Id == id);
    }

    private Task<HttpOutput<DataOutput<VerifyTwoFactorAuthCommandOutput?>?>> VerifyAsync(
        Person person, string? code = null, string? recoveryCode = null) =>
        Gateway.PostAsync<DataOutput<VerifyTwoFactorAuthCommandOutput?>>(
            "/api/auth/2fa/verify",
            new VerifyTwoFactorAuthCommand
            {
                ChallengeToken = TestTokens.ForMfaPending(person.PublicId, (int)Roles.SystemAdmin),
                Code = code,
                RecoveryCode = recoveryCode
            });

    // FR-2F-10: a challenge is redeemed once

    [FunctionalFact]
    public async Task GivenOneChallenge_WhenRedeemedInParallelWithDifferentRecoveryCodes_ThenExactlyOneTokenIsIssued()
    {
        // Given one outstanding challenge and five good recovery codes — five, the challenge's guess
        // budget, so every request is compared and only redemption can tell them apart
        var person = await SeedPersonAsync("parallel-challenge");
        var twoFactorAuth = await SeedTwoFactorAsync(person);
        var codes = new List<string>();

        for (var i = 0; i < 5; i++)
        {
            codes.Add(await SeedRecoveryCodeAsync(twoFactorAuth.Id));
        }

        // When each is presented on the same challenge at once
        var responses = await InParallelAsync(codes.Count, index => VerifyAsync(person, recoveryCode: codes[index]));

        // Then one full token, and the challenge is spent
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
        Assert.All(responses.Where(response => response.StatusCode != HttpStatusCode.OK),
            response => Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode));
        Assert.Null((await ReloadTwoFactorAsync(twoFactorAuth.Id)).ChallengeId);
    }

    // UC-38 / UC-40: a recovery code is spent once

    [FunctionalFact]
    public async Task GivenOneRecoveryCode_WhenRegeneratingInParallel_ThenExactlyOneSetIsIssued()
    {
        // Given an active configuration and one recovery code, used as the factor for UC-40 — which
        // has no challenge to serialise it, so only the code itself can
        var person = await SeedPersonAsync("parallel-regenerate");
        var twoFactorAuth = await SeedTwoFactorAsync(person);
        var recoveryCode = await SeedRecoveryCodeAsync(twoFactorAuth.Id);
        Authorize(TestTokens.For(person.PublicId, (int)Roles.SystemAdmin));

        // When it is presented eight times at once
        var responses = await InParallelAsync(8, _ =>
            Gateway.PostAsync<DataOutput<RegenerateRecoveryCodesCommandOutput?>>(
                "/api/auth/2fa/recovery-codes/regenerate",
                new RegenerateRecoveryCodesCommand { RecoveryCode = recoveryCode }));

        // Then one regeneration, and the configuration holds exactly the ten codes it returned
        var winner = Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);

        await using var context = db.CreateContext();
        var stored = await context.TwoFactorRecoveryCodes
            .Where(x => x.TwoFactorAuthId == twoFactorAuth.Id)
            .ToListAsync();

        Assert.Equal(10, stored.Count);
        Assert.All(winner.Body!.Data!.RecoveryCodes, code =>
            Assert.Contains(stored, row => row.CodeHash.SequenceEqual(SHA256.HashData(Encoding.UTF8.GetBytes(code)))));
    }

    // FR-2F-14: an app code is accepted once

    [FunctionalFact]
    public async Task GivenOneAppCode_WhenConfirmingSetupInParallel_ThenExactlyOneConfirms()
    {
        // Given a pending App setup made through the API, so its secret is genuinely encrypted
        var person = await SeedPersonAsync("parallel-totp");
        Authorize(TestTokens.For(person.PublicId, (int)Roles.SystemAdmin));

        var enable = await Gateway.PostAsync<DataOutput<EnableTwoFactorAuthCommandOutput?>>(
            "/api/auth/2fa/enable", new EnableTwoFactorAuthCommand { Methods = ["App"] });
        var uri = enable.Body!.Data!.OtpAuthUri!;
        var secret = uri[(uri.IndexOf("secret=", StringComparison.Ordinal) + "secret=".Length)..].Split('&')[0];
        var appCode = new Totp(Base32Encoding.ToBytes(secret)).ComputeTotp();

        // When the one current code is presented eight times at once
        var responses = await InParallelAsync(8, _ =>
            Gateway.PostAsync<DataOutput<ConfirmTwoFactorAuthCommandOutput?>>(
                "/api/auth/2fa/confirm", new ConfirmTwoFactorAuthCommand { AppCode = appCode }));

        // Then one confirmation and one set of recovery codes — not one per request that read the
        // last accepted step before any of them recorded it
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);

        await using var context = db.CreateContext();
        var twoFactorAuth = await context.TwoFactorAuths.SingleAsync(x => x.PersonId == person.Id);

        Assert.True(twoFactorAuth.IsActive);
        Assert.Equal(10, await context.TwoFactorRecoveryCodes.CountAsync(x => x.TwoFactorAuthId == twoFactorAuth.Id));
    }

    // UC-13 and UC-14: a token is spent once

    [FunctionalFact]
    public async Task GivenOneResetToken_WhenSpentInParallel_ThenExactlyOnePasswordIsSet()
    {
        // Given one live reset token
        var person = await SeedPersonAsync("parallel-reset");
        const string token = "ParallelResetToken0123456789abcdefABCDEF0123456789";

        await using (var context = db.CreateContext())
        {
            context.PasswordResetTokens.Add(new PasswordResetToken
            {
                PersonId = person.Id,
                TokenHash = SingleUseTokenHash.Of(token),
                ExpiresAt = DateTime.UtcNow.AddHours(1)
            });
            await context.SaveChangesAsync();
        }

        // When six requests each try to set a different password with it
        var responses = await InParallelAsync(6, index =>
            Gateway.PostAsync<DataOutput<ResetPasswordCommandOutput?>>(
                "/api/auth/password-reset",
                new ResetPasswordCommand { Token = token, NewPassword = $"Str0ng-Parallel-{index}!" }));

        // Then one of them did, and every other was told the token was already used
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
        Assert.All(responses.Where(response => response.StatusCode != HttpStatusCode.OK), response =>
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains(AuthMessages.TokenAlreadyUsed, response.Body!.Errors);
        });
    }

    [FunctionalFact]
    public async Task GivenOneVerificationToken_WhenSpentInParallel_ThenExactlyOneSucceeds()
    {
        // Given one live verification token
        var person = await SeedPersonAsync("parallel-verify");
        const string token = "ParallelVerifyToken0123456789abcdefABCDEF012345678";

        await using (var context = db.CreateContext())
        {
            context.EmailVerificationTokens.Add(new EmailVerificationToken
            {
                PersonId = person.Id,
                TokenHash = SingleUseTokenHash.Of(token),
                ExpiresAt = DateTime.UtcNow.AddHours(1)
            });
            await context.SaveChangesAsync();
        }

        // When
        var responses = await InParallelAsync(8, _ =>
            Gateway.PostAsync<DataOutput<VerifyEmailCommandOutput?>>(
                "/api/auth/verify-email", new VerifyEmailCommand { Token = token }));

        // Then
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
        Assert.All(responses.Where(response => response.StatusCode != HttpStatusCode.OK),
            response => Assert.Contains(AuthMessages.TokenAlreadyUsed, response.Body!.Errors));
    }

    // Budgets: charged before the comparison, so a burst cannot outrun them

    [FunctionalFact]
    public async Task GivenTenWrongPasswordsAtOnce_WhenLoggingIn_ThenTheAccountLocks()
    {
        // Given FR-AU-09's threshold of ten, reached in one burst. Counted after the derivation, the
        // ten requests all read a count of zero and all wrote one: the account never locked.
        var person = await SeedPersonAsync("parallel-lockout");

        // When
        var responses = await InParallelAsync(10, _ =>
            Gateway.PostAsync<DataOutput<LoginCommandOutput?>>(
                "/api/auth/login", new LoginCommand { Email = person.Email, Password = "Wr0ng-Password!" }));

        // Then
        Assert.All(responses, response => Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode));

        await using var context = db.CreateContext();
        var stored = await context.Persons.SingleAsync(x => x.Id == person.Id);

        Assert.NotNull(stored.LockedOutUntil);
        Assert.True(stored.LockedOutUntil > DateTime.UtcNow);
        Assert.Equal(0, stored.FailedLoginAttempts);
    }

    [FunctionalFact]
    public async Task GivenTenWrongEmailCodesAtOnce_WhenConfirmingSetup_ThenOnlyFiveAreComparedAndTheCodeIsRetired()
    {
        // Given a pending email setup with one live code (FR-2F-13: five guesses)
        var person = await SeedPersonAsync("parallel-email-code");
        var twoFactorAuth = await SeedTwoFactorAsync(person, isActive: false, appEnabled: false, emailEnabled: true);

        await using (var context = db.CreateContext())
        {
            context.TwoFactorEmailCodes.Add(new TwoFactorEmailCode
            {
                TwoFactorAuthId = twoFactorAuth.Id,
                CodeHash = Hash.EncodeWithRandomSalt(EmailCode, out var salt),
                Salt = salt,
                ExpiresAt = DateTime.UtcNow.AddMinutes(10)
            });
            await context.SaveChangesAsync();
        }

        Authorize(TestTokens.For(person.PublicId, (int)Roles.SystemAdmin));

        // When ten wrong guesses arrive at once
        var responses = await InParallelAsync(10, index =>
            Gateway.PostAsync<DataOutput<ConfirmTwoFactorAuthCommandOutput?>>(
                "/api/auth/2fa/confirm", new ConfirmTwoFactorAuthCommand { EmailCode = $"00000{index}" }));

        // Then all are refused, exactly the budget was charged, and the code is retired
        Assert.DoesNotContain(responses, response => response.StatusCode == HttpStatusCode.OK);

        await using var verification = db.CreateContext();
        var code = await verification.TwoFactorEmailCodes.SingleAsync(x => x.TwoFactorAuthId == twoFactorAuth.Id);

        Assert.Equal(TwoFactorEmailCodeVerification.MaxFailedAttempts, code.FailedAttempts);
        Assert.True(code.Used);
    }

    [FunctionalFact]
    public async Task GivenEightResendsAtOnce_WhenReissuingTheChallengeCode_ThenOnlyThreeCodesAreIssued()
    {
        // Given an outstanding challenge on an email configuration (FR-2F-13: three reissues)
        var person = await SeedPersonAsync("parallel-resend");
        var twoFactorAuth = await SeedTwoFactorAsync(person, appEnabled: false, emailEnabled: true);
        var challengeToken = TestTokens.ForMfaPending(person.PublicId, (int)Roles.SystemAdmin);

        // When
        var responses = await InParallelAsync(8, _ =>
            Gateway.PostAsync<DataOutput<ResendTwoFactorChallengeCodeCommandOutput?>>(
                "/api/auth/2fa/challenge/resend", new ResendTwoFactorChallengeCodeCommand { ChallengeToken = challengeToken }));

        // Then every caller gets UC-46's one answer, and the budget held
        Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
        Assert.Equal(ResendTwoFactorChallengeCodeCommandHandler.MaxReissuesPerChallenge,
            (await ReloadTwoFactorAsync(twoFactorAuth.Id)).EmailCodeReissueCount);

        await using var context = db.CreateContext();
        Assert.Equal(ResendTwoFactorChallengeCodeCommandHandler.MaxReissuesPerChallenge,
            await context.TwoFactorEmailCodes.CountAsync(x => x.TwoFactorAuthId == twoFactorAuth.Id));
    }

    // FR-2F-17: five guesses per challenge with an app code or a recovery code

    [FunctionalFact]
    public async Task GivenEightWrongAppCodesAtOnce_WhenVerifying_ThenFiveAreChargedAndTheChallengeIsRetired()
    {
        // Given an outstanding challenge on an app configuration
        var person = await SeedPersonAsync("parallel-challenge-cap");
        var twoFactorAuth = await SeedTwoFactorAsync(person);

        // When
        var responses = await InParallelAsync(8, _ => VerifyAsync(person, code: "000000"));

        // Then
        Assert.All(responses, response => Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode));

        var stored = await ReloadTwoFactorAsync(twoFactorAuth.Id);

        Assert.Equal(VerifyTwoFactorAuthCommandHandler.MaxGuessesPerChallenge, stored.ChallengeAttempts);
        Assert.Null(stored.ChallengeId);
    }

    [FunctionalFact]
    public async Task GivenFiveWrongRecoveryCodes_WhenTheRightOneFollows_ThenTheChallengeIsAlreadyGone()
    {
        // Given an outstanding challenge and one good recovery code
        var person = await SeedPersonAsync("challenge-cap");
        var twoFactorAuth = await SeedTwoFactorAsync(person);
        var recoveryCode = await SeedRecoveryCodeAsync(twoFactorAuth.Id);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var wrong = await VerifyAsync(person, recoveryCode: $"WRONG-{attempt:000}");
            Assert.Contains(TwoFactorMessages.FactorInvalid, wrong.Body!.Errors);
        }

        // When
        var response = await VerifyAsync(person, recoveryCode: recoveryCode);

        // Then — a fresh password check is the price of further guessing, and the code is unspent
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(TwoFactorMessages.ChallengeTokenInvalid, response.Body!.Errors);

        await using var context = db.CreateContext();
        Assert.False((await context.TwoFactorRecoveryCodes.SingleAsync(x => x.TwoFactorAuthId == twoFactorAuth.Id)).Used);
    }
}
