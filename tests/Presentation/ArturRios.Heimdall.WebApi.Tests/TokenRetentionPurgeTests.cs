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
        var passwordResetTokens = new EfRepository<PasswordResetToken, long>(context);
        var emailVerificationTokens = new EfRepository<EmailVerificationToken, long>(context);
        var twoFactorEmailCodes = new EfRepository<TwoFactorEmailCode, long>(context);
        var pendingRegistrations = new EfRepository<PendingRegistration, long>(context);
        var applicationCredentials = new EfRepository<ApplicationCredential, long>(context);
        var attemptNoticeBudgets = new EfRepository<AttemptNoticeBudget, long>(context);
        var googleIdTokenUses = new EfRepository<GoogleIdTokenUse, string>(context);
        var scopeOwnerInvitations = new EfRepository<ScopeOwnerInvitation, long>(context);

        return new PurgeExpiredTokensCommandHandler(
            passwordResetTokens, passwordResetTokens,
            emailVerificationTokens, emailVerificationTokens,
            twoFactorEmailCodes, twoFactorEmailCodes,
            pendingRegistrations, pendingRegistrations,
            applicationCredentials, applicationCredentials,
            attemptNoticeBudgets, attemptNoticeBudgets,
            googleIdTokenUses, googleIdTokenUses,
            scopeOwnerInvitations, scopeOwnerInvitations,
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

        var scope = new Scope { PublicId = Guid.NewGuid(), Name = $"scope-{Guid.NewGuid():N}" };
        context.Scopes.Add(scope);
        await context.SaveChangesAsync();

        PendingRegistration Pending(DateTime expiresAt) => new()
        {
            ScopeId = scope.Id, Name = "Pending", Email = $"pending-{Guid.NewGuid():N}@functional.test",
            PasswordHash = [1], Salt = [2], TokenHash = Guid.NewGuid().ToString("N"),
            ExpiresAt = expiresAt, CreatedAt = expiresAt.AddHours(-24)
        };

        var expiredPending = Pending(longExpired);
        var livePending = Pending(live);
        context.PendingRegistrations.AddRange(expiredPending, livePending);
        await context.SaveChangesAsync();

        // When — the handler runs over EF repositories, so the whole expression is translated to SQL
        var output = await Handler(context).HandleAsync(new PurgeExpiredTokensCommand());

        // Then — the run succeeded and reached all four tables
        Assert.True(output.Success);
        Assert.True(output.Data!.PasswordResetTokensPurged >= 1);
        Assert.True(output.Data.EmailVerificationTokensPurged >= 1);
        Assert.True(output.Data.TwoFactorEmailCodesPurged >= 1);
        Assert.True(output.Data.PendingRegistrationsPurged >= 1);

        // And each row past its retention period is gone from the database
        await using var verification = fixture.CreateContext();

        Assert.False(await verification.PasswordResetTokens.AnyAsync(x => x.Id == expiredReset.Id));
        Assert.False(await verification.EmailVerificationTokens.AnyAsync(x => x.Id == expiredVerification.Id));
        Assert.False(await verification.TwoFactorEmailCodes.AnyAsync(x => x.Id == expiredCode.Id));
        Assert.False(await verification.PendingRegistrations.AnyAsync(x => x.Id == expiredPending.Id));

        // While the live token is untouched — a purge never takes a token from a caller still holding it
        Assert.True(await verification.PasswordResetTokens.AnyAsync(x => x.Id == liveReset.Id));
        Assert.True(await verification.PendingRegistrations.AnyAsync(x => x.Id == livePending.Id));

        // And the person the tokens belonged to is not itself a purge target
        Assert.True(await verification.Persons.AnyAsync(x => x.Id == person.Id));
    }

    [FunctionalFact]
    public async Task GivenNoticeBudgetsWhoseWindowEndedLongAgo_WhenPurging_ThenOnlyThoseAreRemoved()
    {
        // Given (FR-SR-14) one budget whose window ended a month ago and one still counting
        await using var context = fixture.CreateContext();

        var ended = new AttemptNoticeBudget
        {
            AddressHash = AttemptNoticeBudget.AddressHashOf($"ended-{Guid.NewGuid():N}@functional.test"),
            NoticesSent = 3,
            WindowEndsAt = DateTime.UtcNow.AddDays(-30)
        };
        var open = new AttemptNoticeBudget
        {
            AddressHash = AttemptNoticeBudget.AddressHashOf($"open-{Guid.NewGuid():N}@functional.test"),
            NoticesSent = 1,
            WindowEndsAt = DateTime.UtcNow.AddHours(12)
        };

        context.AttemptNoticeBudgets.AddRange(ended, open);
        await context.SaveChangesAsync();

        // When
        var output = await Handler(context).HandleAsync(new PurgeExpiredTokensCommand());

        // Then
        Assert.True(output.Success);
        Assert.True(output.Data!.AttemptNoticeBudgetsPurged >= 1);

        await using var verification = fixture.CreateContext();

        Assert.False(await verification.AttemptNoticeBudgets.AnyAsync(x => x.Id == ended.Id));
        Assert.True(await verification.AttemptNoticeBudgets.AnyAsync(x => x.Id == open.Id));
    }

    [FunctionalFact]
    public async Task GivenUsedGoogleIdTokensThatExpiredLongAgo_WhenPurging_ThenOnlyThoseAreRemoved()
    {
        // Given (FR-GO-21) the record of a token that expired a month ago, and of one still live
        await using var context = fixture.CreateContext();

        var expired = new GoogleIdTokenUse
        {
            Id = GoogleIdTokenUse.KeyOf(Guid.NewGuid().ToString("N"), "unused"), ExpiresAt = DateTime.UtcNow.AddDays(-30)
        };
        var live = new GoogleIdTokenUse
        {
            Id = GoogleIdTokenUse.KeyOf(Guid.NewGuid().ToString("N"), "unused"), ExpiresAt = DateTime.UtcNow.AddMinutes(30)
        };

        context.GoogleIdTokenUses.AddRange(expired, live);
        await context.SaveChangesAsync();

        // When
        var output = await Handler(context).HandleAsync(new PurgeExpiredTokensCommand());

        // Then
        Assert.True(output.Success);
        Assert.True(output.Data!.GoogleIdTokenUsesPurged >= 1);

        await using var verification = fixture.CreateContext();

        Assert.False(await verification.GoogleIdTokenUses.AnyAsync(x => x.Id == expired.Id));
        Assert.True(await verification.GoogleIdTokenUses.AnyAsync(x => x.Id == live.Id));
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

    [FunctionalFact]
    public async Task GivenSpentApplicationCredentials_WhenPurging_ThenOnlyTheLiveOneRemains()
    {
        // Given an application with one credential revoked well past the grace, one expired well
        // past it, and one live
        var (_, _, application) = await ApplicationSeeds.SeedOwnedApplicationAsync(fixture);
        var longAgo = DateTime.UtcNow.AddDays(-30);
        var (revoked, _) = await ApplicationSeeds.SeedCredentialAsync(fixture, application, revokedAt: longAgo);
        var (expired, _) = await ApplicationSeeds.SeedCredentialAsync(fixture, application, expiresAt: longAgo);
        var (live, _) = await ApplicationSeeds.SeedCredentialAsync(fixture, application);

        // When
        await using var context = fixture.CreateContext();
        var output = await Handler(context).HandleAsync(new PurgeExpiredTokensCommand());

        // Then
        Assert.True(output.Success);
        Assert.True(output.Data!.ApplicationCredentialsPurged >= 2);
        var remaining = await ApplicationSeeds.CredentialsOfAsync(fixture, application);
        Assert.Equal([live.PublicId], remaining.Select(x => x.PublicId));
        Assert.DoesNotContain(remaining, x => x.PublicId == revoked.PublicId || x.PublicId == expired.PublicId);
    }

    [FunctionalFact]
    public async Task GivenScopeOwnerInvitationsPastNinetyDays_WhenPurging_ThenTheyAreRemovedAndRecentOnesKept()
    {
        // Given — a scope and invitee of this test's own
        await using var context = fixture.CreateContext();
        var scope = new Scope { PublicId = Guid.NewGuid(), Name = $"scope-{Guid.NewGuid():N}" };
        context.Scopes.Add(scope);
        var invitee = new Person
        {
            PublicId = Guid.NewGuid(), Name = "Invitee", Email = $"invitee-{Guid.NewGuid():N}@functional.test",
            PasswordHash = [1], Salt = [2], RoleId = (long)Roles.ScopeAdmin
        };
        context.Persons.Add(invitee);
        await context.SaveChangesAsync();
        var now = DateTime.UtcNow;
        var old = new ScopeOwnerInvitation
        {
            PublicId = Guid.NewGuid(), ScopeId = scope.Id, InviteePersonId = invitee.Id,
            CreatedAt = now.AddDays(-100), ExpiresAt = now.AddDays(-93), EndedAt = now.AddDays(-91),
            Outcome = ScopeOwnerInvitationOutcomes.Declined
        };
        var lapsed = new ScopeOwnerInvitation
        {
            PublicId = Guid.NewGuid(), ScopeId = scope.Id, InviteePersonId = invitee.Id,
            CreatedAt = now.AddDays(-98), ExpiresAt = now.AddDays(-91)
        };
        var recent = new ScopeOwnerInvitation
        {
            PublicId = Guid.NewGuid(), ScopeId = scope.Id, InviteePersonId = invitee.Id,
            CreatedAt = now.AddDays(-12), ExpiresAt = now.AddDays(-5), EndedAt = now.AddDays(-10),
            Outcome = ScopeOwnerInvitationOutcomes.Withdrawn
        };
        context.ScopeOwnerInvitations.AddRange(old, lapsed, recent);
        await context.SaveChangesAsync();

        // When
        var output = await Handler(context).HandleAsync(new PurgeExpiredTokensCommand());

        // Then
        Assert.True(output.Success);
        Assert.True(output.Data!.ScopeOwnerInvitationsPurged >= 2);
        await using var check = fixture.CreateContext();
        var left = await check.ScopeOwnerInvitations.AsNoTracking()
            .Where(x => x.ScopeId == scope.Id).Select(x => x.PublicId).ToListAsync();
        Assert.Equal([recent.PublicId], left);
    }
}
