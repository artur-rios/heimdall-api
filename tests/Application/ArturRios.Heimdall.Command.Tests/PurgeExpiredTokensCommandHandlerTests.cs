using ArturRios.Heimdall.Command.Handlers;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Heimdall.Shared.Retention;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Mock;

namespace ArturRios.Heimdall.Command.Tests;

// Unit tests for PurgeExpiredTokensCommandHandler (NFR-19): the main flow removing rows from all
// three single-use token tables once they are past expiry plus the grace period, the two rows that
// must survive a run (a live token, and one that has expired or been used but is still inside the
// grace period), the batch bound, and the empty run that is a success rather than a failure.
//
// The grace period is the behaviour worth pinning hardest. UC-13 answers a presented token with
// three distinct outcomes, and purging on expiry alone would collapse TokenExpired and
// TokenAlreadyUsed into TokenInvalid — telling a person following a stale link that their token
// never existed. Two of these tests exist to keep that from being optimised away.
public class PurgeExpiredTokensCommandHandlerTests
{
    private static readonly TimeSpan Grace = TimeSpan.FromDays(7);

    private static DataRetentionOptions Retention(int batchSize = 500) => new()
    {
        SingleUseTokenGrace = Grace,
        PurgeBatchSize = batchSize
    };

    private static async Task<PasswordResetToken> SeedPasswordResetTokenAsync(
        AsyncFakeRepository<PasswordResetToken> tokens, DateTime expiresAt, bool used = false)
    {
        var token = new PasswordResetToken
        {
            PersonId = 1,
            TokenHash = Guid.NewGuid().ToString("N"),
            ExpiresAt = expiresAt,
            Used = used
        };

        await tokens.CreateAsync(token);

        return token;
    }

    private static async Task<EmailVerificationToken> SeedEmailVerificationTokenAsync(
        AsyncFakeRepository<EmailVerificationToken> tokens, DateTime expiresAt, bool used = false)
    {
        var token = new EmailVerificationToken
        {
            PersonId = 1,
            TokenHash = Guid.NewGuid().ToString("N"),
            ExpiresAt = expiresAt,
            Used = used
        };

        await tokens.CreateAsync(token);

        return token;
    }

    private static async Task<TwoFactorEmailCode> SeedTwoFactorEmailCodeAsync(
        AsyncFakeRepository<TwoFactorEmailCode> codes, DateTime expiresAt, bool used = false)
    {
        var code = new TwoFactorEmailCode
        {
            TwoFactorAuthId = 1,
            CodeHash = [1, 2, 3],
            Salt = [4, 5, 6],
            ExpiresAt = expiresAt,
            Used = used,
            CreatedAt = expiresAt.AddMinutes(-10)
        };

        await codes.CreateAsync(code);

        return code;
    }

    private static PurgeExpiredTokensCommandHandler Handler(
        AsyncFakeRepository<PasswordResetToken> passwordResetTokens,
        AsyncFakeRepository<EmailVerificationToken> emailVerificationTokens,
        AsyncFakeRepository<TwoFactorEmailCode> twoFactorEmailCodes,
        DataRetentionOptions? retention = null) =>
        new(passwordResetTokens, passwordResetTokens,
            emailVerificationTokens, emailVerificationTokens,
            twoFactorEmailCodes, twoFactorEmailCodes,
            retention ?? Retention());

    private static async Task<int> CountAsync<T>(AsyncFakeRepository<T> repository)
        where T : ArturRios.Data.Relational.Core.Entities.Entity =>
        (await repository.GetAllAsync()).Data!.Count();

    [UnitFact]
    public async Task GivenTokensPastTheGracePeriod_WhenPurging_ThenEveryTableIsCleared()
    {
        // Given one row per table, all expired well beyond the grace period
        var passwordResetTokens = new AsyncFakeRepository<PasswordResetToken>();
        var emailVerificationTokens = new AsyncFakeRepository<EmailVerificationToken>();
        var twoFactorEmailCodes = new AsyncFakeRepository<TwoFactorEmailCode>();
        var longExpired = DateTime.UtcNow - Grace - TimeSpan.FromDays(1);

        await SeedPasswordResetTokenAsync(passwordResetTokens, longExpired);
        await SeedEmailVerificationTokenAsync(emailVerificationTokens, longExpired);
        await SeedTwoFactorEmailCodeAsync(twoFactorEmailCodes, longExpired);

        // When
        var output = await Handler(passwordResetTokens, emailVerificationTokens, twoFactorEmailCodes)
            .HandleAsync(new PurgeExpiredTokensCommand());

        // Then — each table reports its row removed, and each row is gone
        Assert.True(output.Success);
        Assert.Equal(RetentionMessages.ExpiredTokensPurged, output.Messages.First());
        Assert.Equal(1, output.Data!.PasswordResetTokensPurged);
        Assert.Equal(1, output.Data.EmailVerificationTokensPurged);
        Assert.Equal(1, output.Data.TwoFactorEmailCodesPurged);
        Assert.Equal(3, output.Data.TotalPurged);
        Assert.Equal(0, await CountAsync(passwordResetTokens));
        Assert.Equal(0, await CountAsync(emailVerificationTokens));
        Assert.Equal(0, await CountAsync(twoFactorEmailCodes));
    }

    [UnitFact]
    public async Task GivenATokenExpiredInsideTheGracePeriod_WhenPurging_ThenItIsKept()
    {
        // Given a token that expired an hour ago — past its life, nowhere near past its retention
        var passwordResetTokens = new AsyncFakeRepository<PasswordResetToken>();
        var emailVerificationTokens = new AsyncFakeRepository<EmailVerificationToken>();
        var twoFactorEmailCodes = new AsyncFakeRepository<TwoFactorEmailCode>();

        await SeedPasswordResetTokenAsync(passwordResetTokens, DateTime.UtcNow.AddHours(-1));

        // When
        var output = await Handler(passwordResetTokens, emailVerificationTokens, twoFactorEmailCodes)
            .HandleAsync(new PurgeExpiredTokensCommand());

        // Then — UC-13 can still answer TokenExpired rather than TokenInvalid
        Assert.True(output.Success);
        Assert.Equal(0, output.Data!.TotalPurged);
        Assert.Equal(1, await CountAsync(passwordResetTokens));
    }

    [UnitFact]
    public async Task GivenAUsedTokenInsideTheGracePeriod_WhenPurging_ThenItIsKept()
    {
        // Given a token consumed a moment ago: consumption does not shorten its retention, because
        // UC-13's TokenAlreadyUsed answer depends on the row still being there
        var passwordResetTokens = new AsyncFakeRepository<PasswordResetToken>();
        var emailVerificationTokens = new AsyncFakeRepository<EmailVerificationToken>();
        var twoFactorEmailCodes = new AsyncFakeRepository<TwoFactorEmailCode>();

        await SeedPasswordResetTokenAsync(passwordResetTokens, DateTime.UtcNow.AddMinutes(30), used: true);

        // When
        var output = await Handler(passwordResetTokens, emailVerificationTokens, twoFactorEmailCodes)
            .HandleAsync(new PurgeExpiredTokensCommand());

        // Then
        Assert.True(output.Success);
        Assert.Equal(0, output.Data!.TotalPurged);
        Assert.Equal(1, await CountAsync(passwordResetTokens));
    }

    [UnitFact]
    public async Task GivenALiveToken_WhenPurging_ThenItIsUntouched()
    {
        // Given a person mid-recovery, mid-verification and mid-second-factor
        var passwordResetTokens = new AsyncFakeRepository<PasswordResetToken>();
        var emailVerificationTokens = new AsyncFakeRepository<EmailVerificationToken>();
        var twoFactorEmailCodes = new AsyncFakeRepository<TwoFactorEmailCode>();

        await SeedPasswordResetTokenAsync(passwordResetTokens, DateTime.UtcNow.AddHours(1));
        await SeedEmailVerificationTokenAsync(emailVerificationTokens, DateTime.UtcNow.AddDays(1));
        await SeedTwoFactorEmailCodeAsync(twoFactorEmailCodes, DateTime.UtcNow.AddMinutes(10));

        // When
        var output = await Handler(passwordResetTokens, emailVerificationTokens, twoFactorEmailCodes)
            .HandleAsync(new PurgeExpiredTokensCommand());

        // Then — no run can ever take a token out from under a caller who still holds it
        Assert.True(output.Success);
        Assert.Equal(0, output.Data!.TotalPurged);
        Assert.Equal(1, await CountAsync(passwordResetTokens));
        Assert.Equal(1, await CountAsync(emailVerificationTokens));
        Assert.Equal(1, await CountAsync(twoFactorEmailCodes));
    }

    [UnitFact]
    public async Task GivenMoreRowsThanTheBatchSize_WhenPurging_ThenTheRunIsBounded()
    {
        // Given five purgeable rows and a batch size of two (SRD §6.3.2 — a purge is delete pressure
        // on tables the login path writes to, so a backlog drains over runs rather than in one)
        var passwordResetTokens = new AsyncFakeRepository<PasswordResetToken>();
        var emailVerificationTokens = new AsyncFakeRepository<EmailVerificationToken>();
        var twoFactorEmailCodes = new AsyncFakeRepository<TwoFactorEmailCode>();
        var longExpired = DateTime.UtcNow - Grace - TimeSpan.FromDays(1);

        for (var i = 0; i < 5; i++)
        {
            await SeedPasswordResetTokenAsync(passwordResetTokens, longExpired.AddMinutes(i));
        }

        var handler = Handler(
            passwordResetTokens, emailVerificationTokens, twoFactorEmailCodes, Retention(batchSize: 2));

        // When
        var first = await handler.HandleAsync(new PurgeExpiredTokensCommand());

        // Then — two rows this run, three left for the next
        Assert.Equal(2, first.Data!.PasswordResetTokensPurged);
        Assert.Equal(3, await CountAsync(passwordResetTokens));

        // And the backlog drains across subsequent runs
        await handler.HandleAsync(new PurgeExpiredTokensCommand());
        var third = await handler.HandleAsync(new PurgeExpiredTokensCommand());

        Assert.Equal(1, third.Data!.PasswordResetTokensPurged);
        Assert.Equal(0, await CountAsync(passwordResetTokens));
    }

    [UnitFact]
    public async Task GivenNothingToPurge_WhenPurging_ThenTheRunSucceedsWithNoCounts()
    {
        // Given empty tables — the state most runs find
        var passwordResetTokens = new AsyncFakeRepository<PasswordResetToken>();
        var emailVerificationTokens = new AsyncFakeRepository<EmailVerificationToken>();
        var twoFactorEmailCodes = new AsyncFakeRepository<TwoFactorEmailCode>();

        // When
        var output = await Handler(passwordResetTokens, emailVerificationTokens, twoFactorEmailCodes)
            .HandleAsync(new PurgeExpiredTokensCommand());

        // Then — a no-op is a success, so the audit trail does not fill with refusals that never
        // happened
        Assert.True(output.Success);
        Assert.Empty(output.Errors);
        Assert.Equal(0, output.Data!.TotalPurged);
    }
}
