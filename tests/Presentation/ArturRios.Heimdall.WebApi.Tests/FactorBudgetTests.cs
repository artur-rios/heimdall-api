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
using ArturRios.Heimdall.WebApi.Tests.Support;
using ArturRios.Output;
using ArturRios.Util.Hashing;
using ArturRios.Util.Http;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Functional;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Heimdall.WebApi.Tests;

// Functional tests for the two per-account factor budgets, against PostgreSQL: the second-factor
// budget at sign-in (FR-AU-12), which a fresh login does not reset, and the re-authentication budget
// under a bearer token (FR-AU-13), which never touches the sign-in budgets. What only the database can
// show is here: that each budget is one conditional write, so a burst cannot outrun it; that the lock
// lengths and the window are computed in the write itself; and that a whole-row save cannot put a
// count back.
//
// Each test stays at or under ten requests, the per-address rate limit on these endpoints, so what
// is asserted is the budget rather than the limiter.
[Collection(nameof(FunctionalCollection))]
public class FactorBudgetTests(PostgresFixture db) : WebApiTest<Program>(EnvironmentType.Local)
{
    private const string Password = "Str0ng-Budget-Pass!";

    private static string UniqueEmail(string prefix) => $"{prefix}-{Guid.NewGuid():N}@test.local";

    private async Task<Person> SeedPersonAsync(string prefix, Action<Person>? configure = null)
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
        configure?.Invoke(person);
        context.Persons.Add(person);
        await context.SaveChangesAsync();
        return person;
    }

    private async Task<TwoFactorAuth> SeedTwoFactorAsync(Person person, bool appEnabled = true)
    {
        await using var context = db.CreateContext();
        var twoFactorAuth = new TwoFactorAuth
        {
            PersonId = person.Id,
            IsActive = true,
            AppEnabled = appEnabled,
            EmailEnabled = !appEnabled,
            // Not a real Data-Protection payload, so every app code is refused — a wrong guess
            // without having to know which codes are wrong at this instant.
            TotpSecretEncrypted = appEnabled ? [1, 2, 3, 4] : null,
            ChallengeId = TestTokens.SeededChallengeId
        };
        context.TwoFactorAuths.Add(twoFactorAuth);
        await context.SaveChangesAsync();
        return twoFactorAuth;
    }

    private async Task<string> SeedRecoveryCodeAsync(long twoFactorAuthId)
    {
        var plaintext = $"BUDG-{Guid.NewGuid():N}"[..9].ToUpperInvariant();

        await using var context = db.CreateContext();
        context.TwoFactorRecoveryCodes.Add(new TwoFactorRecoveryCode
        {
            TwoFactorAuthId = twoFactorAuthId,
            CodeHash = SHA256.HashData(Encoding.UTF8.GetBytes(plaintext))
        });
        await context.SaveChangesAsync();

        return plaintext;
    }

    private async Task<Person> ReloadAsync(Person person)
    {
        await using var context = db.CreateContext();
        return await context.Persons.SingleAsync(x => x.Id == person.Id);
    }

    private Task<HttpOutput<DataOutput<LoginCommandOutput?>?>> LoginAsync(Person person) =>
        Gateway.PostAsync<DataOutput<LoginCommandOutput?>>(
            "/api/auth/login", new LoginCommand { Email = person.Email, Password = Password });

    private Task<HttpOutput<DataOutput<VerifyTwoFactorAuthCommandOutput?>?>> VerifyAsync(
        string challengeToken, string? code = null, string? recoveryCode = null) =>
        Gateway.PostAsync<DataOutput<VerifyTwoFactorAuthCommandOutput?>>(
            "/api/auth/2fa/verify",
            new VerifyTwoFactorAuthCommand { ChallengeToken = challengeToken, Code = code, RecoveryCode = recoveryCode });

    private Task<HttpOutput<DataOutput<DisableTwoFactorAuthCommandOutput?>?>> DisableAsync(
        string password, string? code = null, string? recoveryCode = null) =>
        Gateway.PostAsync<DataOutput<DisableTwoFactorAuthCommandOutput?>>(
            "/api/auth/2fa/disable",
            new DisableTwoFactorAuthCommand { Password = password, Code = code, RecoveryCode = recoveryCode });

    /// <summary>
    ///     Starts <paramref name="count" /> copies of a request at the same moment and waits for all of
    ///     them, under a patient hash gate — see <c>ConcurrentSingleUseTests</c> for why.
    /// </summary>
    private static async Task<T[]> InParallelAsync<T>(int count, Func<int, Task<T>> request)
    {
        var original = PasswordHashGate.Shared;
        PasswordHashGate.Shared = new PasswordHashGate(original.MaxConcurrent, TimeSpan.FromMinutes(2));

        try
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
        finally
        {
            PasswordHashGate.Shared = original;
        }
    }

    private static void AssertCloseTo(DateTime expected, DateTime? actual) =>
        Assert.InRange(actual!.Value, expected.AddSeconds(-5), expected.AddSeconds(5));

    /// <summary>
    ///     That <paramref name="actual" /> is <paramref name="offset" /> after some moment between
    ///     <paramref name="before" /> and <paramref name="after" /> — the times taken either side of
    ///     the requests that set it, since a burst queued behind the hash gate can take seconds.
    /// </summary>
    private static void AssertSetBetween(DateTime before, DateTime after, TimeSpan offset, DateTime? actual) =>
        Assert.InRange(actual!.Value, before + offset - TimeSpan.FromSeconds(1), after + offset + TimeSpan.FromSeconds(1));

    // FR-AU-12: the second-factor budget at sign-in

    [FunctionalFact]
    public async Task GivenFailuresFromEarlierChallenges_WhenFreshLoginsFollow_ThenTheyAccumulateAndTheRightCodeIsRefused()
    {
        // Given five failures an earlier login left behind, and a good recovery code
        var person = await SeedPersonAsync("budget-across", p => p.FailedSecondFactorAttempts = 5);
        var twoFactorAuth = await SeedTwoFactorAsync(person);
        var recoveryCode = await SeedRecoveryCodeAsync(twoFactorAuth.Id);

        var first = await LoginAsync(person);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        for (var attempt = 0; attempt < 4; attempt++)
        {
            await VerifyAsync(first.Body!.Data!.ChallengeToken!, code: "000000");
        }

        // When a fresh login — a correct password — is followed by the tenth failure and the right code
        var second = await LoginAsync(person);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(9, (await ReloadAsync(person)).FailedSecondFactorAttempts);

        var before = DateTime.UtcNow;
        var tenth = await VerifyAsync(second.Body!.Data!.ChallengeToken!, code: "000000");
        var right = await VerifyAsync(second.Body!.Data!.ChallengeToken!, recoveryCode: recoveryCode);

        // Then the right code is refused exactly as the wrong one was
        Assert.Equal(HttpStatusCode.Unauthorized, right.StatusCode);
        Assert.Equal(tenth.StatusCode, right.StatusCode);
        Assert.Equal([TwoFactorMessages.FactorInvalid], right.Body!.Errors);
        Assert.Equal(tenth.Body!.Errors, right.Body.Errors);

        var stored = await ReloadAsync(person);
        AssertSetBetween(before, DateTime.UtcNow, TimeSpan.FromMinutes(15), stored.SecondFactorLockedOutUntil);
        Assert.Equal(1, stored.SecondFactorLockouts);
        Assert.Equal(0, stored.FailedSecondFactorAttempts);

        await using var context = db.CreateContext();
        Assert.False((await context.TwoFactorRecoveryCodes.SingleAsync(x => x.TwoFactorAuthId == twoFactorAuth.Id)).Used);
    }

    [FunctionalFact]
    public async Task GivenEightFailures_WhenFiveWrongCodesArriveAtOnce_ThenExactlyOneLockIsSet()
    {
        // Given an email-only configuration, whose guesses the challenge does not count, two short of
        // the limit
        var person = await SeedPersonAsync("budget-burst", p => p.FailedSecondFactorAttempts = 8);
        await SeedTwoFactorAsync(person, appEnabled: false);
        var challengeToken = TestTokens.ForMfaPending(person.PublicId, (int)Roles.SystemAdmin);

        // When
        var before = DateTime.UtcNow;
        var responses = await InParallelAsync(5, _ => VerifyAsync(challengeToken, code: "000000"));

        // Then every one is refused alike, and the burst locked once rather than once per request
        Assert.All(responses, response =>
        {
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal([TwoFactorMessages.FactorInvalid], response.Body!.Errors);
        });

        var stored = await ReloadAsync(person);
        AssertSetBetween(before, DateTime.UtcNow, TimeSpan.FromMinutes(15), stored.SecondFactorLockedOutUntil);
        Assert.Equal(1, stored.SecondFactorLockouts);
        Assert.Equal(0, stored.FailedSecondFactorAttempts);
    }

    [FunctionalFact]
    public async Task GivenTwoEarlierLockouts_WhenTheLimitIsReachedAgain_ThenTheWriteLocksForAnHour()
    {
        // Given
        var person = await SeedPersonAsync("budget-escalate", p =>
        {
            p.FailedSecondFactorAttempts = SecondFactorLockout.MaxFailedAttempts - 1;
            p.SecondFactorLockouts = 2;
        });
        var now = DateTime.UtcNow;

        // When
        await using var context = db.CreateContext();
        var current = await context.Persons.SingleAsync(x => x.Id == person.Id);
        var reserved = await new AtomicWrites(context).TryReserveSecondFactorAttemptAsync(
            current, SecondFactorLockout.MaxFailedAttempts, now, SecondFactorLockout.LockoutEndsFrom(now));

        // Then
        Assert.True(reserved);

        var stored = await ReloadAsync(person);
        AssertCloseTo(now.AddHours(1), stored.SecondFactorLockedOutUntil);
        Assert.Equal(3, stored.SecondFactorLockouts);
        Assert.Equal(0, stored.FailedSecondFactorAttempts);
    }

    [FunctionalFact]
    public async Task GivenManyEarlierLockouts_WhenTheLimitIsReachedAgain_ThenTheWriteLocksForADay()
    {
        // Given
        var person = await SeedPersonAsync("budget-cap", p =>
        {
            p.FailedSecondFactorAttempts = SecondFactorLockout.MaxFailedAttempts - 1;
            p.SecondFactorLockouts = 40;
        });
        var now = DateTime.UtcNow;

        // When
        await using var context = db.CreateContext();
        var current = await context.Persons.SingleAsync(x => x.Id == person.Id);
        await new AtomicWrites(context).TryReserveSecondFactorAttemptAsync(
            current, SecondFactorLockout.MaxFailedAttempts, now, SecondFactorLockout.LockoutEndsFrom(now));

        // Then
        AssertCloseTo(now.AddHours(24), (await ReloadAsync(person)).SecondFactorLockedOutUntil);
    }

    [FunctionalFact]
    public async Task GivenALockThatHasRunOut_WhenTheRightCodeIsPresented_ThenTheBudgetIsCleared()
    {
        // Given
        var person = await SeedPersonAsync("budget-clear", p =>
        {
            p.FailedSecondFactorAttempts = 4;
            p.SecondFactorLockouts = 3;
            p.SecondFactorLockedOutUntil = DateTime.UtcNow.AddMinutes(-1);
        });
        var twoFactorAuth = await SeedTwoFactorAsync(person);
        var recoveryCode = await SeedRecoveryCodeAsync(twoFactorAuth.Id);

        // When
        var response = await VerifyAsync(
            TestTokens.ForMfaPending(person.PublicId, (int)Roles.SystemAdmin), recoveryCode: recoveryCode);

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var stored = await ReloadAsync(person);
        Assert.Equal(0, stored.FailedSecondFactorAttempts);
        Assert.Equal(0, stored.SecondFactorLockouts);
        Assert.Null(stored.SecondFactorLockedOutUntil);
    }

    // FR-AU-13: the re-authentication budget

    [FunctionalFact]
    public async Task GivenSevenWrongPasswordsAtOnce_WhenDisablingTwoFactor_ThenReauthenticationPausesAndSignInStillWorks()
    {
        // Given
        var person = await SeedPersonAsync("reauth-burst");
        var twoFactorAuth = await SeedTwoFactorAsync(person);
        var recoveryCode = await SeedRecoveryCodeAsync(twoFactorAuth.Id);
        Authorize(TestTokens.For(person.PublicId, (int)Roles.SystemAdmin));

        // When
        var before = DateTime.UtcNow;
        var responses = await InParallelAsync(7, _ => DisableAsync("Wr0ng-Password!"));

        // Then every one is refused alike, and the fifth failure paused the checks
        Assert.All(responses, response =>
        {
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal([AuthMessages.PasswordOrCodeIncorrect], response.Body!.Errors);
        });

        var stored = await ReloadAsync(person);
        AssertSetBetween(before, DateTime.UtcNow, TimeSpan.FromMinutes(15), stored.ReauthenticationPausedUntil);
        Assert.Equal(0, stored.FailedReauthenticationAttempts);
        Assert.Null(stored.ReauthenticationWindowStartedAt);

        // Then — and the right password and code are refused the same way while it lasts
        var right = await DisableAsync(Password, recoveryCode: recoveryCode);

        Assert.Equal(HttpStatusCode.Unauthorized, right.StatusCode);
        Assert.Equal([AuthMessages.PasswordOrCodeIncorrect], right.Body!.Errors);

        await using (var context = db.CreateContext())
        {
            Assert.True(await context.TwoFactorAuths.AnyAsync(x => x.Id == twoFactorAuth.Id));
        }

        // Then — but the owner can still sign in: neither sign-in budget was charged
        stored = await ReloadAsync(person);
        Assert.Equal(0, stored.FailedLoginAttempts);
        Assert.Null(stored.LockedOutUntil);
        Assert.Equal(0, stored.FailedSecondFactorAttempts);
        Assert.Null(stored.SecondFactorLockedOutUntil);

        var login = await LoginAsync(person);

        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.True(login.Body!.Data!.RequiresTwoFactor);
    }

    [FunctionalFact]
    public async Task GivenAWrongPasswordAndAWrongCode_WhenDisablingTwoFactor_ThenTheAnswersAreIdentical()
    {
        // Given
        var person = await SeedPersonAsync("reauth-identical");
        await SeedTwoFactorAsync(person);
        Authorize(TestTokens.For(person.PublicId, (int)Roles.SystemAdmin));

        // When
        var wrongPassword = await DisableAsync("Wr0ng-Password!", code: "000000");
        var wrongCode = await DisableAsync(Password, code: "000000");

        // Then
        Assert.Equal(HttpStatusCode.Unauthorized, wrongPassword.StatusCode);
        Assert.Equal(wrongPassword.StatusCode, wrongCode.StatusCode);
        Assert.Equal([AuthMessages.PasswordOrCodeIncorrect], wrongPassword.Body!.Errors);
        Assert.Equal(wrongPassword.Body.Errors, wrongCode.Body!.Errors);
        Assert.Equal(wrongPassword.Body.Messages, wrongCode.Body.Messages);
    }

    [FunctionalFact]
    public async Task GivenFourFailuresInAWindowThatHasRunOut_WhenAnotherIsCharged_ThenTheWriteStartsANewWindow()
    {
        // Given
        var person = await SeedPersonAsync("reauth-window-out", p =>
        {
            p.FailedReauthenticationAttempts = ReauthenticationBudget.MaxFailures - 1;
            p.ReauthenticationWindowStartedAt = DateTime.UtcNow.AddMinutes(-16);
        });
        var now = DateTime.UtcNow;

        // When
        await using var context = db.CreateContext();
        var current = await context.Persons.SingleAsync(x => x.Id == person.Id);
        var reserved = await new AtomicWrites(context).TryReserveReauthenticationAsync(
            current, ReauthenticationBudget.MaxFailures, now, now - ReauthenticationBudget.Window,
            now + ReauthenticationBudget.Pause);

        // Then
        Assert.True(reserved);

        var stored = await ReloadAsync(person);
        Assert.Null(stored.ReauthenticationPausedUntil);
        Assert.Equal(1, stored.FailedReauthenticationAttempts);
        AssertCloseTo(now, stored.ReauthenticationWindowStartedAt);
    }

    [FunctionalFact]
    public async Task GivenFourFailuresInAnOpenWindow_WhenAnotherIsCharged_ThenTheWritePausesAndClosesTheWindow()
    {
        // Given
        var person = await SeedPersonAsync("reauth-window-open", p =>
        {
            p.FailedReauthenticationAttempts = ReauthenticationBudget.MaxFailures - 1;
            p.ReauthenticationWindowStartedAt = DateTime.UtcNow.AddMinutes(-14);
        });
        var now = DateTime.UtcNow;

        // When
        await using var context = db.CreateContext();
        var current = await context.Persons.SingleAsync(x => x.Id == person.Id);
        await new AtomicWrites(context).TryReserveReauthenticationAsync(
            current, ReauthenticationBudget.MaxFailures, now, now - ReauthenticationBudget.Window,
            now + ReauthenticationBudget.Pause);

        // Then
        var stored = await ReloadAsync(person);
        AssertCloseTo(now.AddMinutes(15), stored.ReauthenticationPausedUntil);
        Assert.Equal(0, stored.FailedReauthenticationAttempts);
        Assert.Null(stored.ReauthenticationWindowStartedAt);
    }

    [FunctionalFact]
    public async Task GivenEarlierFailures_WhenTheRightPasswordEnablesTwoFactor_ThenTheyAreCleared()
    {
        // Given
        var person = await SeedPersonAsync("reauth-clear", p =>
        {
            p.FailedReauthenticationAttempts = 3;
            p.ReauthenticationWindowStartedAt = DateTime.UtcNow.AddMinutes(-1);
        });
        Authorize(TestTokens.For(person.PublicId, (int)Roles.SystemAdmin));

        // When
        var response = await Gateway.PostAsync<DataOutput<EnableTwoFactorAuthCommandOutput?>>(
            "/api/auth/2fa/enable",
            new EnableTwoFactorAuthCommand { Methods = ["App"], CurrentPassword = Password });

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var stored = await ReloadAsync(person);
        Assert.Equal(0, stored.FailedReauthenticationAttempts);
        Assert.Null(stored.ReauthenticationWindowStartedAt);
    }

    // Both budgets are written only by their targeted writes

    [FunctionalFact]
    public async Task GivenACopyReadBeforeTheBudgetsWereCharged_WhenSavedAsAWholeRow_ThenTheChargesStand()
    {
        // Given one request holding the person as it was, while others charge both budgets
        var person = await SeedPersonAsync("budget-whole-row");

        await using var stale = db.CreateContext();
        var staleCopy = await stale.Persons.SingleAsync(x => x.Id == person.Id);

        await using var charging = db.CreateContext();
        var current = await charging.Persons.SingleAsync(x => x.Id == person.Id);
        var atomicWrites = new AtomicWrites(charging);
        var now = DateTime.UtcNow;

        await atomicWrites.TryReserveSecondFactorAttemptAsync(
            current, SecondFactorLockout.MaxFailedAttempts, now, SecondFactorLockout.LockoutEndsFrom(now));
        await atomicWrites.TryReserveReauthenticationAsync(
            current, 1, now, now - ReauthenticationBudget.Window, now + ReauthenticationBudget.Pause);

        // When the first request saves its copy the way the repositories do — every column
        staleCopy.Name = "Saved later";
        stale.Persons.Update(staleCopy);
        await stale.SaveChangesAsync();

        // Then neither charge is undone, and the rest of the row was still written
        var stored = await ReloadAsync(person);
        Assert.Equal(1, stored.FailedSecondFactorAttempts);
        AssertCloseTo(now.AddMinutes(15), stored.ReauthenticationPausedUntil);
        Assert.Equal("Saved later", stored.Name);
    }

    // A refused charge is a write too, and a row past the limit heals

    /// <summary>
    ///     The row's <c>xmin</c>: the transaction that last wrote it. It moves only when a write
    ///     reaches the row, which is what a refused charge must do to cost what an admitted one does.
    /// </summary>
    private async Task<long> RowVersionAsync(Person person)
    {
        await using var context = db.CreateContext();

        return await context.Database
            .SqlQuery<long>($"SELECT xmin::text::bigint AS \"Value\" FROM heimdall.person WHERE id = {person.Id}")
            .SingleAsync();
    }

    [FunctionalFact]
    public async Task GivenALockedSecondFactor_WhenAReservationIsRefused_ThenTheRowIsStillWrittenAndUnchanged()
    {
        // Given
        var lockedUntil = DateTime.UtcNow.AddMinutes(10);
        var person = await SeedPersonAsync("budget-refused-write", p => p.SecondFactorLockedOutUntil = lockedUntil);
        var version = await RowVersionAsync(person);
        var now = DateTime.UtcNow;

        // When
        await using var context = db.CreateContext();
        var current = await context.Persons.SingleAsync(x => x.Id == person.Id);
        var reserved = await new AtomicWrites(context).TryReserveSecondFactorAttemptAsync(
            current, SecondFactorLockout.MaxFailedAttempts, now, SecondFactorLockout.LockoutEndsFrom(now));

        // Then
        Assert.False(reserved);
        Assert.NotEqual(version, await RowVersionAsync(person));

        var stored = await ReloadAsync(person);
        Assert.Equal(0, stored.FailedSecondFactorAttempts);
        AssertCloseTo(lockedUntil, stored.SecondFactorLockedOutUntil);
    }

    [FunctionalFact]
    public async Task GivenAPausedBudget_WhenAReservationIsRefused_ThenTheRowIsStillWrittenAndUnchanged()
    {
        // Given
        var pausedUntil = DateTime.UtcNow.AddMinutes(10);
        var person = await SeedPersonAsync("reauth-refused-write", p => p.ReauthenticationPausedUntil = pausedUntil);
        var version = await RowVersionAsync(person);
        var now = DateTime.UtcNow;

        // When
        await using var context = db.CreateContext();
        var current = await context.Persons.SingleAsync(x => x.Id == person.Id);
        var reserved = await new AtomicWrites(context).TryReserveReauthenticationAsync(
            current, ReauthenticationBudget.MaxFailures, now, now - ReauthenticationBudget.Window,
            now + ReauthenticationBudget.Pause);

        // Then
        Assert.False(reserved);
        Assert.NotEqual(version, await RowVersionAsync(person));

        var stored = await ReloadAsync(person);
        Assert.Equal(0, stored.FailedReauthenticationAttempts);
        AssertCloseTo(pausedUntil, stored.ReauthenticationPausedUntil);
    }

    [FunctionalFact]
    public async Task GivenACountPastTheLimitWithAnExpiredLock_WhenAReservationIsMade_ThenItIsAdmittedAndLocks()
    {
        // Given a row left past the limit — by a lowered limit, say — whose lock has run out
        var person = await SeedPersonAsync("budget-heal-expired", p =>
        {
            p.FailedSecondFactorAttempts = SecondFactorLockout.MaxFailedAttempts + 2;
            p.SecondFactorLockedOutUntil = DateTime.UtcNow.AddMinutes(-1);
            p.SecondFactorLockouts = 1;
        });
        var now = DateTime.UtcNow;

        // When
        await using var context = db.CreateContext();
        var current = await context.Persons.SingleAsync(x => x.Id == person.Id);
        var reserved = await new AtomicWrites(context).TryReserveSecondFactorAttemptAsync(
            current, SecondFactorLockout.MaxFailedAttempts, now, SecondFactorLockout.LockoutEndsFrom(now));

        // Then it heals: admitted, locked for the next length, and the count starts again
        Assert.True(reserved);

        var stored = await ReloadAsync(person);
        Assert.Equal(0, stored.FailedSecondFactorAttempts);
        Assert.Equal(2, stored.SecondFactorLockouts);
        AssertCloseTo(now.AddMinutes(30), stored.SecondFactorLockedOutUntil);
    }
}
