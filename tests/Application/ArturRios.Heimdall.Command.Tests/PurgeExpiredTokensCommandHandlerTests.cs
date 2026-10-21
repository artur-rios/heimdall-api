using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Heimdall.Command.Handlers;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Heimdall.Shared.Retention;
using ArturRios.Output;
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

    private static DataRetentionOptions Retention(
        int batchSize = 500, int maxBatches = DataRetentionOptions.DefaultMaxBatchesPerTick) => new()
    {
        SingleUseTokenGrace = Grace,
        PurgeBatchSize = batchSize,
        MaxBatchesPerTick = maxBatches
    };

    // A token store whose deletes start failing after the first few, as when the database goes away
    // part-way through a run.
    private sealed class FailingAfterTokenRepository(int succeeding)
        : AsyncFakeRepository<PasswordResetToken, long>, IAsyncRepository<PasswordResetToken, long>
    {
        private int _deletes;

        Task<DataOutput<IEnumerable<long>>> IAsyncRepository<PasswordResetToken, long>.DeleteRangeAsync(
            IEnumerable<long> ids, CancellationToken cancellationToken) =>
            ++_deletes > succeeding
                ? Task.FromResult(DataOutput<IEnumerable<long>>.New.WithError("database unavailable"))
                : DeleteRangeAsync(ids, cancellationToken);
    }

    private static async Task<PasswordResetToken> SeedPasswordResetTokenAsync(
        AsyncFakeRepository<PasswordResetToken, long> tokens, DateTime expiresAt, bool used = false)
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
        AsyncFakeRepository<EmailVerificationToken, long> tokens, DateTime expiresAt, bool used = false)
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
        AsyncFakeRepository<TwoFactorEmailCode, long> codes, DateTime expiresAt, bool used = false)
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
        AsyncFakeRepository<PasswordResetToken, long> passwordResetTokens,
        AsyncFakeRepository<EmailVerificationToken, long> emailVerificationTokens,
        AsyncFakeRepository<TwoFactorEmailCode, long> twoFactorEmailCodes,
        DataRetentionOptions? retention = null,
        AsyncFakeRepository<PendingRegistration, long>? pendingRegistrations = null,
        AsyncFakeRepository<ApplicationCredential, long>? credentials = null,
        AsyncFakeRepository<AttemptNoticeBudget, long>? noticeBudgets = null,
        AsyncFakeRepository<GoogleIdTokenUse, string>? googleIdTokenUses = null,
        AsyncFakeRepository<ScopeOwnerInvitation, long>? invitations = null)
    {
        pendingRegistrations ??= new AsyncFakeRepository<PendingRegistration, long>();
        credentials ??= new AsyncFakeRepository<ApplicationCredential, long>();
        noticeBudgets ??= new AsyncFakeRepository<AttemptNoticeBudget, long>();
        googleIdTokenUses ??= new AsyncFakeRepository<GoogleIdTokenUse, string>();
        invitations ??= new AsyncFakeRepository<ScopeOwnerInvitation, long>();

        return new(passwordResetTokens, passwordResetTokens,
            emailVerificationTokens, emailVerificationTokens,
            twoFactorEmailCodes, twoFactorEmailCodes,
            pendingRegistrations, pendingRegistrations,
            credentials, credentials,
            noticeBudgets, noticeBudgets,
            googleIdTokenUses, googleIdTokenUses,
            invitations, invitations,
            retention ?? Retention());
    }

    private static async Task<PendingRegistration> SeedPendingRegistrationAsync(
        AsyncFakeRepository<PendingRegistration, long> pending, DateTime expiresAt)
    {
        var registration = new PendingRegistration
        {
            ScopeId = 1,
            Name = "Pending",
            Email = $"{Guid.NewGuid():N}@test.local",
            PasswordHash = [1],
            Salt = [2],
            TokenHash = Guid.NewGuid().ToString("N"),
            ExpiresAt = expiresAt,
            CreatedAt = expiresAt.AddHours(-24)
        };

        await pending.CreateAsync(registration);

        return registration;
    }

    private static async Task<int> CountAsync<T>(AsyncFakeRepository<T, long> repository)
        where T : ArturRios.Data.Relational.Core.Entities.Entity<long> =>
        (await repository.GetAllAsync()).Data!.Count();

    [UnitFact]
    public async Task GivenTokensPastTheGracePeriod_WhenPurging_ThenEveryTableIsCleared()
    {
        // Given one row per table, all expired well beyond the grace period
        var passwordResetTokens = new AsyncFakeRepository<PasswordResetToken, long>();
        var emailVerificationTokens = new AsyncFakeRepository<EmailVerificationToken, long>();
        var twoFactorEmailCodes = new AsyncFakeRepository<TwoFactorEmailCode, long>();
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
        var passwordResetTokens = new AsyncFakeRepository<PasswordResetToken, long>();
        var emailVerificationTokens = new AsyncFakeRepository<EmailVerificationToken, long>();
        var twoFactorEmailCodes = new AsyncFakeRepository<TwoFactorEmailCode, long>();

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
        var passwordResetTokens = new AsyncFakeRepository<PasswordResetToken, long>();
        var emailVerificationTokens = new AsyncFakeRepository<EmailVerificationToken, long>();
        var twoFactorEmailCodes = new AsyncFakeRepository<TwoFactorEmailCode, long>();

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
        var passwordResetTokens = new AsyncFakeRepository<PasswordResetToken, long>();
        var emailVerificationTokens = new AsyncFakeRepository<EmailVerificationToken, long>();
        var twoFactorEmailCodes = new AsyncFakeRepository<TwoFactorEmailCode, long>();

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
        var passwordResetTokens = new AsyncFakeRepository<PasswordResetToken, long>();
        var emailVerificationTokens = new AsyncFakeRepository<EmailVerificationToken, long>();
        var twoFactorEmailCodes = new AsyncFakeRepository<TwoFactorEmailCode, long>();
        var longExpired = DateTime.UtcNow - Grace - TimeSpan.FromDays(1);

        for (var i = 0; i < 5; i++)
        {
            await SeedPasswordResetTokenAsync(passwordResetTokens, longExpired.AddMinutes(i));
        }

        var handler = Handler(
            passwordResetTokens, emailVerificationTokens, twoFactorEmailCodes,
            Retention(batchSize: 2, maxBatches: 1));

        // When
        var first = await handler.HandleAsync(new PurgeExpiredTokensCommand());

        // Then — two rows this run, three left for the next, and the run says so
        Assert.Equal(2, first.Data!.PasswordResetTokensPurged);
        Assert.Equal(3, await CountAsync(passwordResetTokens));
        Assert.Equal(3, first.Data.Backlog);

        // And the backlog drains across subsequent runs
        await handler.HandleAsync(new PurgeExpiredTokensCommand());
        var third = await handler.HandleAsync(new PurgeExpiredTokensCommand());

        Assert.Equal(1, third.Data!.PasswordResetTokensPurged);
        Assert.Equal(0, await CountAsync(passwordResetTokens));
        Assert.Equal(0, third.Data.Backlog);
    }

    // #206: a run repeats its batch while the batch comes back full, up to MaxBatchesPerTick.

    [UnitFact]
    public async Task GivenABacklogOfThreeFullBatches_WhenPurging_ThenItDrainsInOneRun()
    {
        // Given six purgeable rows in each of two tables and a batch of two
        var passwordResetTokens = new AsyncFakeRepository<PasswordResetToken, long>();
        var emailVerificationTokens = new AsyncFakeRepository<EmailVerificationToken, long>();
        var twoFactorEmailCodes = new AsyncFakeRepository<TwoFactorEmailCode, long>();
        var longExpired = DateTime.UtcNow - Grace - TimeSpan.FromDays(1);

        for (var i = 0; i < 6; i++)
        {
            await SeedPasswordResetTokenAsync(passwordResetTokens, longExpired.AddMinutes(i));
            await SeedEmailVerificationTokenAsync(emailVerificationTokens, longExpired.AddMinutes(i));
        }

        // When
        var output = await Handler(
                passwordResetTokens, emailVerificationTokens, twoFactorEmailCodes, Retention(batchSize: 2))
            .HandleAsync(new PurgeExpiredTokensCommand());

        // Then — every row went in the one run, and nothing is left
        Assert.True(output.Success);
        Assert.Equal(6, output.Data!.PasswordResetTokensPurged);
        Assert.Equal(6, output.Data.EmailVerificationTokensPurged);
        Assert.Equal(12, output.Data.TotalPurged);
        Assert.Equal(0, output.Data.Backlog);
        Assert.Equal(0, await CountAsync(passwordResetTokens));
        Assert.Equal(0, await CountAsync(emailVerificationTokens));
    }

    [UnitFact]
    public async Task GivenMoreFullBatchesThanTheCap_WhenPurging_ThenTheRunStopsAtTheCapAndReportsWhatIsLeft()
    {
        // Given seven purgeable rows, a batch of two and at most two batches a run
        var passwordResetTokens = new AsyncFakeRepository<PasswordResetToken, long>();
        var longExpired = DateTime.UtcNow - Grace - TimeSpan.FromDays(1);

        for (var i = 0; i < 7; i++)
        {
            await SeedPasswordResetTokenAsync(passwordResetTokens, longExpired.AddMinutes(i));
        }

        var handler = Handler(
            passwordResetTokens, new AsyncFakeRepository<EmailVerificationToken, long>(),
            new AsyncFakeRepository<TwoFactorEmailCode, long>(), Retention(batchSize: 2, maxBatches: 2));

        // When
        var first = await handler.HandleAsync(new PurgeExpiredTokensCommand());

        // Then — two batches, and the three rows still due are the backlog
        Assert.Equal(4, first.Data!.PasswordResetTokensPurged);
        Assert.Equal(3, first.Data.Backlog);
        Assert.Equal(3, await CountAsync(passwordResetTokens));

        // And the next run finishes it
        var second = await handler.HandleAsync(new PurgeExpiredTokensCommand());

        Assert.Equal(3, second.Data!.PasswordResetTokensPurged);
        Assert.Equal(0, second.Data.Backlog);
    }

    [UnitFact]
    public async Task GivenADeleteFailsPartWayThroughARun_WhenPurging_ThenTheRowsAlreadyRemovedAreStillCounted()
    {
        // Given six purgeable rows, a batch of two, and a store that fails from its second delete on
        var passwordResetTokens = new FailingAfterTokenRepository(succeeding: 1);
        var longExpired = DateTime.UtcNow - Grace - TimeSpan.FromDays(1);

        for (var i = 0; i < 6; i++)
        {
            await SeedPasswordResetTokenAsync(passwordResetTokens, longExpired.AddMinutes(i));
        }

        // When
        var output = await Handler(
                passwordResetTokens, new AsyncFakeRepository<EmailVerificationToken, long>(),
                new AsyncFakeRepository<TwoFactorEmailCode, long>(), Retention(batchSize: 2))
            .HandleAsync(new PurgeExpiredTokensCommand());

        // Then — the run failed, and still says what it removed before it did
        Assert.False(output.Success);
        Assert.Contains("database unavailable", output.Errors);
        Assert.Equal(2, output.Data!.PasswordResetTokensPurged);
        Assert.Equal(4, await CountAsync(passwordResetTokens));
    }

    [UnitFact]
    public async Task GivenNothingToPurge_WhenPurging_ThenTheRunSucceedsWithNoCounts()
    {
        // Given empty tables — the state most runs find
        var passwordResetTokens = new AsyncFakeRepository<PasswordResetToken, long>();
        var emailVerificationTokens = new AsyncFakeRepository<EmailVerificationToken, long>();
        var twoFactorEmailCodes = new AsyncFakeRepository<TwoFactorEmailCode, long>();

        // When
        var output = await Handler(passwordResetTokens, emailVerificationTokens, twoFactorEmailCodes)
            .HandleAsync(new PurgeExpiredTokensCommand());

        // Then — a no-op is a success, so the audit trail does not fill with refusals that never
        // happened
        Assert.True(output.Success);
        Assert.Empty(output.Errors);
        Assert.Equal(0, output.Data!.TotalPurged);
    }

    [UnitFact]
    public async Task GivenPendingRegistrationsPastTheGracePeriod_WhenPurging_ThenOnlyThoseAreRemoved()
    {
        // Given one pending registration long expired, one expired inside the grace period (the
        // confirmer still answers Expired for it), and one a person may yet confirm
        var pending = new AsyncFakeRepository<PendingRegistration, long>();
        var longExpired = await SeedPendingRegistrationAsync(pending, DateTime.UtcNow - Grace - TimeSpan.FromDays(1));
        var recentlyExpired = await SeedPendingRegistrationAsync(pending, DateTime.UtcNow.AddHours(-1));
        var live = await SeedPendingRegistrationAsync(pending, DateTime.UtcNow.AddHours(1));

        // When
        var output = await Handler(
                new AsyncFakeRepository<PasswordResetToken, long>(),
                new AsyncFakeRepository<EmailVerificationToken, long>(),
                new AsyncFakeRepository<TwoFactorEmailCode, long>(),
                pendingRegistrations: pending)
            .HandleAsync(new PurgeExpiredTokensCommand());

        // Then — the same cutoff as the tokens, counted on its own and in the total
        Assert.True(output.Success);
        Assert.Equal(1, output.Data!.PendingRegistrationsPurged);
        Assert.Equal(1, output.Data.TotalPurged);
        var remaining = pending.Query().Select(x => x.Id).ToList();
        Assert.DoesNotContain(longExpired.Id, remaining);
        Assert.Contains(recentlyExpired.Id, remaining);
        Assert.Contains(live.Id, remaining);
    }

    [UnitFact]
    public async Task GivenUsedGoogleIdTokensThatExpiredBeforeTheGrace_WhenPurging_ThenOnlyThoseAreRemoved()
    {
        // Given (FR-GO-21) a used token that expired before the grace period, one that expired inside
        // it, and one still live
        var uses = new AsyncFakeRepository<GoogleIdTokenUse, string>();
        var stale = new GoogleIdTokenUse { Id = "a", ExpiresAt = DateTime.UtcNow - Grace - TimeSpan.FromDays(1) };
        var recent = new GoogleIdTokenUse { Id = "b", ExpiresAt = DateTime.UtcNow.AddHours(-1) };
        var live = new GoogleIdTokenUse { Id = "c", ExpiresAt = DateTime.UtcNow.AddMinutes(30) };
        await uses.CreateAsync(stale);
        await uses.CreateAsync(recent);
        await uses.CreateAsync(live);

        // When
        var output = await Handler(
                new AsyncFakeRepository<PasswordResetToken, long>(),
                new AsyncFakeRepository<EmailVerificationToken, long>(),
                new AsyncFakeRepository<TwoFactorEmailCode, long>(),
                googleIdTokenUses: uses)
            .HandleAsync(new PurgeExpiredTokensCommand());

        // Then — the tokens' cutoff, counted on its own and in the total
        Assert.True(output.Success);
        Assert.Equal(1, output.Data!.GoogleIdTokenUsesPurged);
        Assert.Equal(1, output.Data.TotalPurged);
        Assert.Equal(["b", "c"], uses.Query().Select(x => x.Id).OrderBy(x => x).ToList());
    }

    [UnitFact]
    public async Task GivenNoticeBudgetsWhoseWindowEndedBeforeTheGrace_WhenPurging_ThenOnlyThoseAreRemoved()
    {
        // Given (FR-SR-14) a budget whose window ended before the grace period, one whose window ended
        // inside it, and one still counting
        var budgets = new AsyncFakeRepository<AttemptNoticeBudget, long>();
        var stale = new AttemptNoticeBudget
        {
            AddressHash = "a", NoticesSent = 3, WindowEndsAt = DateTime.UtcNow - Grace - TimeSpan.FromDays(1)
        };
        var recent = new AttemptNoticeBudget
        {
            AddressHash = "b", NoticesSent = 1, WindowEndsAt = DateTime.UtcNow.AddHours(-1)
        };
        var open = new AttemptNoticeBudget { AddressHash = "c", NoticesSent = 2, WindowEndsAt = DateTime.UtcNow.AddHours(1) };
        await budgets.CreateAsync(stale);
        await budgets.CreateAsync(recent);
        await budgets.CreateAsync(open);

        // When
        var output = await Handler(
                new AsyncFakeRepository<PasswordResetToken, long>(),
                new AsyncFakeRepository<EmailVerificationToken, long>(),
                new AsyncFakeRepository<TwoFactorEmailCode, long>(),
                noticeBudgets: budgets)
            .HandleAsync(new PurgeExpiredTokensCommand());

        // Then — the tokens' cutoff, counted on its own and in the total
        Assert.True(output.Success);
        Assert.Equal(1, output.Data!.AttemptNoticeBudgetsPurged);
        Assert.Equal(1, output.Data.TotalPurged);
        var remaining = budgets.Query().Select(x => x.Id).ToList();
        Assert.DoesNotContain(stale.Id, remaining);
        Assert.Contains(recent.Id, remaining);
        Assert.Contains(open.Id, remaining);
    }

    [UnitFact]
    public async Task GivenSpentCredentialsPastTheGrace_WhenPurging_ThenOnlyThoseAreRemoved()
    {
        // Given — revoked past the grace, expired past the grace, revoked within it, and live
        var cutoff = DateTime.UtcNow - Grace;
        var credentials = new AsyncFakeRepository<ApplicationCredential, long>();
        await credentials.CreateAsync(new ApplicationCredential { Id = 1, SecretHash = "a", RevokedAt = cutoff.AddMinutes(-1) });
        await credentials.CreateAsync(new ApplicationCredential { Id = 2, SecretHash = "b", ExpiresAt = cutoff.AddMinutes(-1) });
        await credentials.CreateAsync(new ApplicationCredential { Id = 3, SecretHash = "c", RevokedAt = cutoff.AddMinutes(5) });
        await credentials.CreateAsync(new ApplicationCredential { Id = 4, SecretHash = "d" });

        // When
        var output = await Handler(
                new AsyncFakeRepository<PasswordResetToken, long>(),
                new AsyncFakeRepository<EmailVerificationToken, long>(),
                new AsyncFakeRepository<TwoFactorEmailCode, long>(),
                credentials: credentials)
            .HandleAsync(new PurgeExpiredTokensCommand());

        // Then
        Assert.Equal(2, output.Data!.ApplicationCredentialsPurged);
        Assert.Equal([3L, 4L], credentials.Query().Select(c => c.Id).OrderBy(id => id));
    }

    [UnitFact]
    public async Task GivenScopeOwnerInvitations_WhenPurging_ThenOnlyThoseEndedOrExpiredOverNinetyDaysAgoGo()
    {
        // Given — FR-SC-18: ended 91 days ago, expired unanswered 91 days ago, ended 10 days ago, still open
        var now = DateTime.UtcNow;
        var invitations = new AsyncFakeRepository<ScopeOwnerInvitation, long>();

        async Task<ScopeOwnerInvitation> SeedAsync(DateTime expiresAt, DateTime? endedAt)
        {
            var invitation = new ScopeOwnerInvitation
            {
                PublicId = Guid.NewGuid(), ScopeId = 1, InviteePersonId = 1, CreatedAt = expiresAt.AddDays(-7),
                ExpiresAt = expiresAt, EndedAt = endedAt,
                Outcome = endedAt is null ? null : ScopeOwnerInvitationOutcomes.Declined
            };
            await invitations.CreateAsync(invitation);
            return invitation;
        }

        var endedLongAgo = await SeedAsync(now.AddDays(-95), now.AddDays(-91));
        var expiredLongAgo = await SeedAsync(now.AddDays(-91), null);
        var endedRecently = await SeedAsync(now.AddDays(-5), now.AddDays(-10));
        var open = await SeedAsync(now.AddDays(5), null);
        var handler = Handler(new(), new(), new(), invitations: invitations);

        // When
        var output = await handler.HandleAsync(new PurgeExpiredTokensCommand());

        // Then
        Assert.True(output.Success);
        Assert.Equal(2, output.Data!.ScopeOwnerInvitationsPurged);
        Assert.Equal(2, output.Data.TotalPurged);
        var left = (await invitations.GetAllAsync()).Data!.Select(x => x.PublicId).ToList();
        Assert.DoesNotContain(endedLongAgo.PublicId, left);
        Assert.DoesNotContain(expiredLongAgo.PublicId, left);
        Assert.Contains(endedRecently.PublicId, left);
        Assert.Contains(open.PublicId, left);
    }

    [UnitFact]
    public async Task GivenAnInvitationVoidedRecentlyThatExpiredLongAgo_WhenPurging_ThenItIsKept()
    {
        // Given — an invitation that lapsed unanswered long ago and was voided only recently, when its
        // invitee was anonymised: once it has ended, the 90 days run from its end
        var now = DateTime.UtcNow;
        var invitations = new AsyncFakeRepository<ScopeOwnerInvitation, long>();
        await invitations.CreateAsync(new ScopeOwnerInvitation
        {
            PublicId = Guid.NewGuid(), ScopeId = 1, InviteePersonId = 1, CreatedAt = now.AddDays(-120),
            ExpiresAt = now.AddDays(-113), EndedAt = now.AddDays(-10), Outcome = ScopeOwnerInvitationOutcomes.Voided
        });
        var handler = Handler(new(), new(), new(), invitations: invitations);

        // When
        var output = await handler.HandleAsync(new PurgeExpiredTokensCommand());

        // Then
        Assert.True(output.Success);
        Assert.Equal(0, output.Data!.ScopeOwnerInvitationsPurged);
        Assert.Single((await invitations.GetAllAsync()).Data!);
    }
}
