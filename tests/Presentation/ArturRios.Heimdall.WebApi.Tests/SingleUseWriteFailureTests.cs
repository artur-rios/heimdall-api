using ArturRios.Data.Relational.Core.Repositories;
using ArturRios.Heimdall.Command.Handlers;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Input.Validation;
using ArturRios.Heimdall.Command.Services;
using ArturRios.Heimdall.Data.Configuration;
using ArturRios.Heimdall.Data.Persistence;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.WebApi.Tests.Support;
using ArturRios.Util.Hashing;
using ArturRios.Util.Test.Attributes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArturRios.Heimdall.WebApi.Tests;

/// <summary>
///     A single-use token, code or pending registration whose follow-on write fails, against the real
///     database (#209): it must still be usable, because it is spent only together with the write it
///     unlocks.
/// </summary>
/// <remarks>
///     Each handler runs over EF repositories on a context whose <see cref="FailingCommand" /> fails the
///     write the token was spent for. The retry runs on a fresh context, as the next request would.
/// </remarks>
[Collection(nameof(FunctionalCollection))]
public class SingleUseWriteFailureTests(PostgresFixture db)
{
    private const string Password = Seeds.Password;
    private const string NewPassword = "Str0ng-Atomic-New-Pass!";
    private const string EmailCode = "123456";

    [FunctionalFact]
    public async Task GivenThePasswordWriteFails_WhenResettingPassword_ThenTheTokenIsStillUsable()
    {
        // Given
        var person = await Seeds.PersonAsync(db, Roles.SystemAdmin, new PersonOptions { Name = "atomic-reset", EmailPrefix = "atomic-reset" });
        var token = SingleUseToken.New();
        await SeedResetTokenAsync(person, token);
        var fault = new FailingCommand("UPDATE", "person");

        // When the write of the password fails
        await using (var context = db.CreateContext(fault))
        {
            await Assert.ThrowsAnyAsync<Exception>(() => ResetPasswordHandler(context)
                .HandleAsync(new ResetPasswordCommand { Token = token, NewPassword = NewPassword }));
        }

        // Then the token was not spent, and the next attempt with it resets the password
        Assert.True(fault.Fired);
        Assert.False((await ResetTokenAsync(token)).Used);

        await using var retry = db.CreateContext();
        var output = await ResetPasswordHandler(retry)
            .HandleAsync(new ResetPasswordCommand { Token = token, NewPassword = NewPassword });

        Assert.True(output.Success, string.Join(" | ", output.Errors));
        Assert.True((await ResetTokenAsync(token)).Used);
    }

    [FunctionalFact]
    public async Task GivenTheVerifiedFlagWriteFails_WhenVerifyingEmail_ThenTheTokenIsStillUsable()
    {
        // Given
        var person = await Seeds.PersonAsync(db, Roles.SystemAdmin, new PersonOptions { Name = "atomic-verify", EmailPrefix = "atomic-verify", EmailVerified = false });
        var token = SingleUseToken.New();

        await using (var seed = db.CreateContext())
        {
            seed.EmailVerificationTokens.Add(new EmailVerificationToken
            {
                PersonId = person.Id, TokenHash = SingleUseTokenHash.Of(token), ExpiresAt = DateTime.UtcNow.AddHours(1)
            });
            await seed.SaveChangesAsync();
        }

        var fault = new FailingCommand("UPDATE", "person");

        // When the write of the verified flag fails
        await using (var context = db.CreateContext(fault))
        {
            await Assert.ThrowsAnyAsync<Exception>(() =>
                VerifyEmailHandler(context).HandleAsync(new VerifyEmailCommand { Token = token }));
        }

        // Then the token was not spent, and the next attempt with it verifies the address
        Assert.True(fault.Fired);
        Assert.False(await VerificationTokenUsedAsync(token));

        await using var retry = db.CreateContext();
        var output = await VerifyEmailHandler(retry).HandleAsync(new VerifyEmailCommand { Token = token });

        Assert.True(output.Success, string.Join(" | ", output.Errors));
        Assert.True(await VerificationTokenUsedAsync(token));
        Assert.True((await PersonAsync(person.Id)).EmailVerified);
    }

    [FunctionalFact]
    public async Task GivenTheActivationFails_WhenConfirmingTwoFactorSetup_ThenTheEmailCodeIsStillUsable()
    {
        // Given a pending email-only setup with one live code
        var person = await Seeds.PersonAsync(db, Roles.SystemAdmin, new PersonOptions { Name = "atomic-2fa", EmailPrefix = "atomic-2fa" });
        long twoFactorAuthId;

        await using (var seed = db.CreateContext())
        {
            var twoFactorAuth = new TwoFactorAuth
            {
                PersonId = person.Id, IsActive = false, AppEnabled = false, EmailEnabled = true
            };
            seed.TwoFactorAuths.Add(twoFactorAuth);
            await seed.SaveChangesAsync();

            seed.TwoFactorEmailCodes.Add(new TwoFactorEmailCode
            {
                TwoFactorAuthId = twoFactorAuth.Id,
                CodeHash = Hash.EncodeWithRandomSalt(EmailCode, out var salt, PasswordHashing.Configuration),
                Salt = salt,
                ExpiresAt = DateTime.UtcNow.AddMinutes(10)
            });
            await seed.SaveChangesAsync();
            twoFactorAuthId = twoFactorAuth.Id;
        }

        var stamp = (await PersonAsync(person.Id)).SecurityStamp;
        var command = new ConfirmTwoFactorAuthCommand { ActingPersonId = person.PublicId, EmailCode = EmailCode };
        var fault = new FailingCommand("UPDATE", "two_factor_auth");

        // When the activation fails
        await using (var context = db.CreateContext(fault))
        {
            var failed = await ConfirmTwoFactorHandler(context).HandleAsync(command);

            Assert.False(failed.Success);
        }

        // Then the code was not spent, no recovery codes or stamp change were kept, and the next attempt
        // with the same code confirms the setup
        Assert.True(fault.Fired);

        await using (var check = db.CreateContext())
        {
            Assert.False((await check.TwoFactorEmailCodes.SingleAsync(x => x.TwoFactorAuthId == twoFactorAuthId)).Used);
            Assert.False(await check.TwoFactorRecoveryCodes.AnyAsync(x => x.TwoFactorAuthId == twoFactorAuthId));
            Assert.False((await check.TwoFactorAuths.SingleAsync(x => x.Id == twoFactorAuthId)).IsActive);
        }

        Assert.Equal(stamp, (await PersonAsync(person.Id)).SecurityStamp);

        await using var retry = db.CreateContext();
        var output = await ConfirmTwoFactorHandler(retry).HandleAsync(command);

        Assert.True(output.Success, string.Join(" | ", output.Errors));
        Assert.Equal(10, output.Data!.RecoveryCodes.Count);
    }

    [FunctionalFact]
    public async Task GivenTheAccountInsertFails_WhenConfirmingARegistration_ThenTheRegistrationIsStillThere()
    {
        // Given
        var scope = await Seeds.ScopeAsync(db, new ScopeOptions { SelfRegistrationEnabled = true });
        var email = UniqueEmail("atomic-pending");
        var token = await SeedPendingAsync(scope, email);
        var fault = new FailingCommand("INSERT", "person");

        // When the account's insert fails
        await using (var context = db.CreateContext(fault))
        {
            var failed = await Confirmer(context).ConfirmAsync(SingleUseTokenHash.Of(token), Password, DateTime.UtcNow);

            Assert.Equal(PendingRegistrationOutcome.Failed, failed.Outcome);
        }

        // Then the registration was not claimed, and confirming it again creates the account
        Assert.True(fault.Fired);
        Assert.True(await PendingExistsAsync(token));

        await using var retry = db.CreateContext();
        var output = await Confirmer(retry).ConfirmAsync(SingleUseTokenHash.Of(token), Password, DateTime.UtcNow);

        Assert.Equal(PendingRegistrationOutcome.Confirmed, output.Outcome);
        Assert.False(await PendingExistsAsync(token));
    }

    [FunctionalFact]
    public async Task GivenTheAddressWasTakenMeanwhile_WhenConfirmingARegistration_ThenTheRegistrationIsKept()
    {
        // Given a registration whose address a User of the scope now holds
        var scope = await Seeds.ScopeAsync(db, new ScopeOptions { SelfRegistrationEnabled = true });
        var email = UniqueEmail("atomic-taken");
        var token = await SeedPendingAsync(scope, email);
        await Seeds.PersonAsync(db, Roles.User, new PersonOptions { Name = "Existing", Email = email, MemberOf = scope });

        // When
        await using var context = db.CreateContext();
        var output = await Confirmer(context).ConfirmAsync(SingleUseTokenHash.Of(token), Password, DateTime.UtcNow);

        // Then it is refused, and the claim was undone with it rather than losing the registration
        Assert.Equal(PendingRegistrationOutcome.Refused, output.Outcome);
        Assert.True(await PendingExistsAsync(token));
    }

    private static ResetPasswordCommandHandler ResetPasswordHandler(AppDbContext context) => new(
        new ResetPasswordCommandValidator(),
        new EfRepository<PasswordResetToken, long>(context),
        new EfRepository<PasswordResetToken, long>(context),
        new AtomicWrites(context),
        new WriteTransactions(context),
        NullLogger<ResetPasswordCommandHandler>.Instance);

    private static VerifyEmailCommandHandler VerifyEmailHandler(AppDbContext context) => new(
        new VerifyEmailCommandValidator(),
        new EfRepository<EmailVerificationToken, long>(context),
        new EfRepository<EmailVerificationToken, long>(context),
        new AtomicWrites(context),
        Confirmer(context),
        new WriteTransactions(context),
        NullLogger<VerifyEmailCommandHandler>.Instance);

    private static ConfirmTwoFactorAuthCommandHandler ConfirmTwoFactorHandler(AppDbContext context) => new(
        new EfRepository<Person, long>(context),
        new EfRepository<TwoFactorAuth, long>(context),
        new EfRepository<TwoFactorAuth, long>(context),
        new EfRepository<TwoFactorEmailCode, long>(context),
        new AtomicWrites(context),
        new EfRepository<TwoFactorRecoveryCode, long>(context),
        new EfRepository<TwoFactorRecoveryCode, long>(context),
        new NoAppCodes(),
        new WriteTransactions(context));

    private static PendingRegistrationConfirmer Confirmer(AppDbContext context) => new(
        new EfRepository<PendingRegistration, long>(context),
        new EfRepository<PendingRegistration, long>(context),
        new EfRepository<Person, long>(context),
        new EfRepository<GoogleUser, long>(context),
        new EfRepository<Person, long>(context),
        new AtomicWrites(context),
        new WriteTransactions(context),
        NullLogger<PendingRegistrationConfirmer>.Instance);

    private static string UniqueEmail(string prefix) => $"{prefix}-{Guid.NewGuid():N}@test.local";

    private async Task SeedResetTokenAsync(Person person, string token)
    {
        await using var context = db.CreateContext();
        context.PasswordResetTokens.Add(new PasswordResetToken
        {
            PersonId = person.Id, TokenHash = SingleUseTokenHash.Of(token), ExpiresAt = DateTime.UtcNow.AddHours(1)
        });
        await context.SaveChangesAsync();
    }

    private async Task<string> SeedPendingAsync(Scope scope, string email)
    {
        var token = SingleUseToken.New();

        var credentials = Seeds.Credentials();
        await using var context = db.CreateContext();
        context.PendingRegistrations.Add(new PendingRegistration
        {
            ScopeId = scope.Id,
            Name = "Pending Person",
            Email = email,
            PasswordHash = credentials.Hash,
            Salt = credentials.Salt,
            TokenHash = SingleUseTokenHash.Of(token),
            ExpiresAt = DateTime.UtcNow.AddHours(1),
            CreatedAt = DateTime.UtcNow
        });
        await context.SaveChangesAsync();

        return token;
    }

    private async Task<PasswordResetToken> ResetTokenAsync(string token)
    {
        await using var context = db.CreateContext();
        var hash = SingleUseTokenHash.Of(token);
        return await context.PasswordResetTokens.SingleAsync(x => x.TokenHash == hash);
    }

    private async Task<bool> VerificationTokenUsedAsync(string token)
    {
        await using var context = db.CreateContext();
        var hash = SingleUseTokenHash.Of(token);
        return (await context.EmailVerificationTokens.SingleAsync(x => x.TokenHash == hash)).Used;
    }

    private async Task<bool> PendingExistsAsync(string token)
    {
        await using var context = db.CreateContext();
        var hash = SingleUseTokenHash.Of(token);
        return await context.PendingRegistrations.AnyAsync(x => x.TokenHash == hash);
    }

    private async Task<Person> PersonAsync(long id)
    {
        await using var context = db.CreateContext();
        return await context.Persons.SingleAsync(x => x.Id == id);
    }

    /// <summary>An authenticator-app check for setups that have no app factor: never called, refuses if it is.</summary>
    private sealed class NoAppCodes : ITotpCodeVerifier
    {
        public Task<bool> VerifyAsync(TwoFactorAuth twoFactorAuth, string? code) => Task.FromResult(false);
    }
}
