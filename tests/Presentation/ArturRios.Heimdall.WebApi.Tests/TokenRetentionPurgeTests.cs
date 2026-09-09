using ArturRios.Data.Relational.Core.Repositories;
using ArturRios.Heimdall.Command.Handlers;
using ArturRios.Heimdall.Data.Configuration;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Shared.Retention;
using ArturRios.Heimdall.WebApi.Tests.Support;
using ArturRios.Util.Test.Attributes;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Heimdall.WebApi.Tests;

/// <summary>
///     The single-use token purge (NFR-19) against the real database. The handler's unit tests run
///     it over in-memory fakes, which prove the policy but not that the query reaches PostgreSQL —
///     the filter, the ordering, the batch bound and the projection to ids are all composed into one
///     expression that EF has to translate, and a fake evaluates it in memory whatever it says. This
///     runs the same handler over EF repositories on the migrated container.
/// </summary>
/// <remarks>
///     The container is shared by the whole functional collection, so the rows here are seeded a
///     month past expiry while the grace period is left at its seven-day default: no other test
///     leaves a token that old behind, so this run can only reach its own. For the same reason the
///     assertions name the rows this test created rather than counting the tables.
/// </remarks>
[Collection(nameof(FunctionalCollection))]
public class TokenRetentionPurgeTests(PostgresFixture fixture)
{
    private static readonly DataRetentionOptions Retention = new()
    {
        SingleUseTokenGrace = TimeSpan.FromDays(7),
        PurgeBatchSize = 500
    };

    private static PurgeExpiredTokensCommandHandler Handler(AppDbContext context)
    {
        var passwordResetTokens = new EfRepository<PasswordResetToken>(context);
        var emailVerificationTokens = new EfRepository<EmailVerificationToken>(context);
        var twoFactorEmailCodes = new EfRepository<TwoFactorEmailCode>(context);

        return new PurgeExpiredTokensCommandHandler(
            passwordResetTokens, passwordResetTokens,
            emailVerificationTokens, emailVerificationTokens,
            twoFactorEmailCodes, twoFactorEmailCodes,
            Retention);
    }

    [FunctionalFact]
    public async Task GivenTokensPastTheirRetentionPeriod_WhenPurging_ThenTheyAreRemovedAndLiveOnesAreKept()
    {
        // Given a person of this test's own, so nothing here depends on or disturbs the rows another
        // class in the shared container seeded
        await using var context = fixture.CreateContext();

        var person = new Person
        {
            PublicId = Guid.NewGuid(),
            Name = "Retention Subject",
            Email = $"retention-{Guid.NewGuid():N}@functional.test",
            RoleId = (long)Roles.User,
            EmailVerified = true
        };

        context.Persons.Add(person);
        await context.SaveChangesAsync();

        var longExpired = DateTime.UtcNow.AddDays(-30);
        var live = DateTime.UtcNow.AddHours(1);

        var expiredReset = new PasswordResetToken
        {
            PersonId = person.Id, TokenHash = Guid.NewGuid().ToString("N"), ExpiresAt = longExpired
        };
        var liveReset = new PasswordResetToken
        {
            PersonId = person.Id, TokenHash = Guid.NewGuid().ToString("N"), ExpiresAt = live
        };
        var expiredVerification = new EmailVerificationToken
        {
            PersonId = person.Id, TokenHash = Guid.NewGuid().ToString("N"), ExpiresAt = longExpired
        };

        var twoFactor = new TwoFactorAuth
        {
            PersonId = person.Id,
            EmailEnabled = true,
            IsActive = true,
            CreatedAt = longExpired,
            UpdatedAt = longExpired
        };

        context.PasswordResetTokens.AddRange(expiredReset, liveReset);
        context.EmailVerificationTokens.Add(expiredVerification);
        context.TwoFactorAuths.Add(twoFactor);
        await context.SaveChangesAsync();

        var expiredCode = new TwoFactorEmailCode
        {
            TwoFactorAuthId = twoFactor.Id,
            CodeHash = [1, 2, 3],
            Salt = [4, 5, 6],
            ExpiresAt = longExpired,
            CreatedAt = longExpired.AddMinutes(-10)
        };

        context.TwoFactorEmailCodes.Add(expiredCode);
        await context.SaveChangesAsync();

        // When — the handler runs over EF repositories, so the whole expression is translated to SQL
        var output = await Handler(context).HandleAsync(new PurgeExpiredTokensCommand());

        // Then — the run succeeded and reached all three tables
        Assert.True(output.Success);
        Assert.True(output.Data!.PasswordResetTokensPurged >= 1);
        Assert.True(output.Data.EmailVerificationTokensPurged >= 1);
        Assert.True(output.Data.TwoFactorEmailCodesPurged >= 1);

        // And each row past its retention period is gone from the database
        await using var verification = fixture.CreateContext();

        Assert.False(await verification.PasswordResetTokens.AnyAsync(x => x.Id == expiredReset.Id));
        Assert.False(await verification.EmailVerificationTokens.AnyAsync(x => x.Id == expiredVerification.Id));
        Assert.False(await verification.TwoFactorEmailCodes.AnyAsync(x => x.Id == expiredCode.Id));

        // While the live token is untouched — a purge never takes a token from a caller still holding it
        Assert.True(await verification.PasswordResetTokens.AnyAsync(x => x.Id == liveReset.Id));

        // And the person the tokens belonged to is not itself a purge target
        Assert.True(await verification.Persons.AnyAsync(x => x.Id == person.Id));
    }

    [FunctionalFact]
    public async Task GivenNothingPastItsRetentionPeriod_WhenPurging_ThenTheRunSucceedsAndRemovesNothing()
    {
        // Given only a live token
        await using var context = fixture.CreateContext();

        var person = new Person
        {
            PublicId = Guid.NewGuid(),
            Name = "Retention Subject",
            Email = $"retention-live-{Guid.NewGuid():N}@functional.test",
            RoleId = (long)Roles.User,
            EmailVerified = true
        };

        context.Persons.Add(person);
        await context.SaveChangesAsync();

        var liveVerification = new EmailVerificationToken
        {
            PersonId = person.Id,
            TokenHash = Guid.NewGuid().ToString("N"),
            ExpiresAt = DateTime.UtcNow.AddDays(1)
        };

        context.EmailVerificationTokens.Add(liveVerification);
        await context.SaveChangesAsync();

        // When
        var output = await Handler(context).HandleAsync(new PurgeExpiredTokensCommand());

        // Then — a run with nothing to do is a success, not a failure
        Assert.True(output.Success);
        Assert.Empty(output.Errors);

        await using var verification = fixture.CreateContext();

        Assert.True(await verification.EmailVerificationTokens.AnyAsync(x => x.Id == liveVerification.Id));
    }
}
