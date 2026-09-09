using ArturRios.Heimdall.Command.Handlers;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Services;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Heimdall.Shared.Retention;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Mock;

namespace ArturRios.Heimdall.Command.Tests;

// Unit tests for AnonymiseExpiredDeletionsCommandHandler (NFR-20): which records a run is due to
// take, which it must leave alone, and what goes with them.
//
// The two deadlines are the behaviour worth pinning hardest. A record deleted because its subject
// asked carries the statutory deadline; one an administrator deleted carries the longer reversal
// window, and a record from before the mechanism existed carries no kind at all and must get the
// longer one — guessing that somebody had requested erasure would invent a request never made.
public class AnonymiseExpiredDeletionsCommandHandlerTests
{
    private static readonly TimeSpan ErasureDeadline = TimeSpan.FromDays(30);
    private static readonly TimeSpan AdministrativeWindow = TimeSpan.FromDays(90);

    private static DataRetentionOptions Retention(int batchSize = 500) => new()
    {
        SubjectErasureDeadline = ErasureDeadline,
        AdministrativeDeletionWindow = AdministrativeWindow,
        PurgeBatchSize = batchSize
    };

    private sealed record Fakes(
        AsyncFakeRepository<Person> Persons,
        AsyncFakeRepository<GoogleUser> GoogleUsers,
        AsyncFakeRepository<PasswordResetToken> PasswordResetTokens,
        AsyncFakeRepository<EmailVerificationToken> EmailVerificationTokens,
        AsyncFakeRepository<TwoFactorAuth> TwoFactorAuths)
    {
        public static Fakes New() => new(new(), new(), new(), new(), new());

        public AnonymiseExpiredDeletionsCommandHandler Handler(DataRetentionOptions? retention = null) =>
            new(Persons, Persons, GoogleUsers, GoogleUsers,
                PasswordResetTokens, PasswordResetTokens,
                EmailVerificationTokens, EmailVerificationTokens,
                TwoFactorAuths, TwoFactorAuths,
                retention ?? Retention());
    }

    private static async Task<Person> SeedPersonAsync(
        Fakes fakes,
        DateTime? deletedAt,
        int? kind,
        bool isDeleted = true,
        DateTime? anonymisedAt = null,
        DateTime? erasureRequestedAt = null,
        DateTime? erasureDueAt = null,
        string? erasureBlockedReason = null,
        Roles role = Roles.User,
        params long[] ownedScopeIds)
    {
        var person = new Person
        {
            PublicId = Guid.NewGuid(),
            Name = "Ada Lovelace",
            Email = $"ada-{Guid.NewGuid():N}@test.local",
            PasswordHash = [1, 2, 3],
            Salt = [4, 5, 6],
            IsDeleted = isDeleted,
            DeletedAt = deletedAt,
            DeletionKind = kind,
            AnonymisedAt = anonymisedAt,
            ErasureRequestedAt = erasureRequestedAt,
            ErasureDueAt = erasureDueAt,
            ErasureBlockedReason = erasureBlockedReason,
            RoleId = (long)role,
            ScopeOwnerships = ownedScopeIds.Select(scopeId => new ScopeOwner { ScopeId = scopeId }).ToList()
        };

        await fakes.Persons.CreateAsync(person);

        return person;
    }

    private static async Task<GoogleUser> SeedGoogleUserAsync(Fakes fakes, DateTime? deletedAt, int? kind)
    {
        var googleUser = new GoogleUser
        {
            PublicId = Guid.NewGuid(),
            GoogleId = Guid.NewGuid().ToString("N"),
            Name = "Ada Lovelace",
            Email = $"ada-{Guid.NewGuid():N}@test.local",
            IsDeleted = true,
            DeletedAt = deletedAt,
            DeletionKind = kind,
            ScopeId = 1
        };

        await fakes.GoogleUsers.CreateAsync(googleUser);

        return googleUser;
    }

    [UnitFact]
    public async Task GivenAnAdministrativeDeletionPastItsWindow_WhenAnonymising_ThenItIsAnonymised()
    {
        var fakes = Fakes.New();
        var person = await SeedPersonAsync(
            fakes, DateTime.UtcNow - AdministrativeWindow - TimeSpan.FromDays(1),
            (int)DeletionKinds.Administrative);

        var output = await fakes.Handler().HandleAsync(new AnonymiseExpiredDeletionsCommand());

        Assert.True(output.Success);
        Assert.Equal(RetentionMessages.ExpiredDeletionsAnonymised, output.Messages.First());
        Assert.Equal(1, output.Data!.PersonsAnonymised);
        Assert.Equal(IdentityAnonymiser.AnonymisedName, person.Name);
        Assert.NotNull(person.AnonymisedAt);
    }

    [UnitFact]
    public async Task GivenAnAdministrativeDeletionInsideItsWindow_WhenAnonymising_ThenItIsKept()
    {
        // 60 days in: past the statutory deadline, but nobody asked — the reversal window is what
        // applies, and an administrator can still undo this
        var fakes = Fakes.New();
        var person = await SeedPersonAsync(
            fakes, DateTime.UtcNow.AddDays(-60), (int)DeletionKinds.Administrative);

        var output = await fakes.Handler().HandleAsync(new AnonymiseExpiredDeletionsCommand());

        Assert.True(output.Success);
        Assert.Equal(0, output.Data!.TotalAnonymised);
        Assert.Equal("Ada Lovelace", person.Name);
        Assert.Null(person.AnonymisedAt);
    }

    [UnitFact]
    public async Task GivenARequestedErasurePastTheStatutoryDeadline_WhenAnonymising_ThenItIsAnonymised()
    {
        // The same 60 days, deleted because the subject asked. GDPR Art. 12(3) allows a month, so
        // this one is overdue where the administrative deletion above was not.
        var fakes = Fakes.New();
        var person = await SeedPersonAsync(
            fakes, DateTime.UtcNow.AddDays(-60), (int)DeletionKinds.SubjectRequested);

        var output = await fakes.Handler().HandleAsync(new AnonymiseExpiredDeletionsCommand());

        Assert.Equal(1, output.Data!.PersonsAnonymised);
        Assert.Equal(IdentityAnonymiser.AnonymisedName, person.Name);
    }

    [UnitFact]
    public async Task GivenARequestedErasureInsideTheStatutoryDeadline_WhenAnonymising_ThenItIsKept()
    {
        var fakes = Fakes.New();
        var person = await SeedPersonAsync(
            fakes, DateTime.UtcNow.AddDays(-10), (int)DeletionKinds.SubjectRequested);

        var output = await fakes.Handler().HandleAsync(new AnonymiseExpiredDeletionsCommand());

        Assert.Equal(0, output.Data!.TotalAnonymised);
        Assert.Null(person.AnonymisedAt);
    }

    [UnitFact]
    public async Task GivenADeletionWithNoRecordedKind_WhenAnonymising_ThenTheLongerWindowApplies()
    {
        // Rows the migration backfilled. Treating an unknown kind as a subject request would apply
        // the shorter deadline to a request nobody made.
        var fakes = Fakes.New();
        var insideLongerWindow = await SeedPersonAsync(fakes, DateTime.UtcNow.AddDays(-60), kind: null);
        var pastLongerWindow = await SeedPersonAsync(fakes, DateTime.UtcNow.AddDays(-120), kind: null);

        var output = await fakes.Handler().HandleAsync(new AnonymiseExpiredDeletionsCommand());

        Assert.Equal(1, output.Data!.PersonsAnonymised);
        Assert.Equal("Ada Lovelace", insideLongerWindow.Name);
        Assert.Equal(IdentityAnonymiser.AnonymisedName, pastLongerWindow.Name);
    }

    [UnitFact]
    public async Task GivenADeletionWithNoRecordedDate_WhenAnonymising_ThenItIsSkipped()
    {
        // A window that cannot be shown to have elapsed is not one this pass acts on.
        var fakes = Fakes.New();
        var person = await SeedPersonAsync(fakes, deletedAt: null, (int)DeletionKinds.Administrative);

        var output = await fakes.Handler().HandleAsync(new AnonymiseExpiredDeletionsCommand());

        Assert.Equal(0, output.Data!.TotalAnonymised);
        Assert.Equal("Ada Lovelace", person.Name);
    }

    [UnitFact]
    public async Task GivenALiveIdentity_WhenAnonymising_ThenItIsUntouched()
    {
        // The oldest CreatedAt in the database is not a deletion. Only a deleted record is due.
        var fakes = Fakes.New();
        var person = await SeedPersonAsync(
            fakes, DateTime.UtcNow.AddDays(-1000), (int)DeletionKinds.Administrative, isDeleted: false);

        var output = await fakes.Handler().HandleAsync(new AnonymiseExpiredDeletionsCommand());

        Assert.Equal(0, output.Data!.TotalAnonymised);
        Assert.Equal("Ada Lovelace", person.Name);
    }

    [UnitFact]
    public async Task GivenAnAlreadyAnonymisedRecord_WhenAnonymising_ThenItIsNotTakenAgain()
    {
        var fakes = Fakes.New();
        var alreadyDone = DateTime.UtcNow.AddDays(-5);
        var person = await SeedPersonAsync(
            fakes, DateTime.UtcNow.AddDays(-200), (int)DeletionKinds.Administrative,
            anonymisedAt: alreadyDone);

        var output = await fakes.Handler().HandleAsync(new AnonymiseExpiredDeletionsCommand());

        Assert.Equal(0, output.Data!.TotalAnonymised);
        Assert.Equal(alreadyDone, person.AnonymisedAt);
    }

    [UnitFact]
    public async Task GivenADuePerson_WhenAnonymising_ThenTheirTokensAndTwoFactorGoWithThem()
    {
        // Erasing the name while keeping the TOTP secret and a reset token addressed to the mailbox
        // just disowned would erase the label and keep the material.
        var fakes = Fakes.New();
        var person = await SeedPersonAsync(
            fakes, DateTime.UtcNow.AddDays(-200), (int)DeletionKinds.Administrative);

        await fakes.PasswordResetTokens.CreateAsync(new PasswordResetToken
        {
            PersonId = person.Id, TokenHash = "a", ExpiresAt = DateTime.UtcNow.AddDays(1)
        });
        await fakes.EmailVerificationTokens.CreateAsync(new EmailVerificationToken
        {
            PersonId = person.Id, TokenHash = "b", ExpiresAt = DateTime.UtcNow.AddDays(1)
        });
        await fakes.TwoFactorAuths.CreateAsync(new TwoFactorAuth
        {
            PersonId = person.Id, AppEnabled = true, IsActive = true, TotpSecretEncrypted = [9, 9, 9]
        });

        var output = await fakes.Handler().HandleAsync(new AnonymiseExpiredDeletionsCommand());

        Assert.True(output.Success);
        Assert.Equal(3, output.Data!.DependentsRemoved);
        Assert.Empty((await fakes.PasswordResetTokens.GetAllAsync()).Data!);
        Assert.Empty((await fakes.EmailVerificationTokens.GetAllAsync()).Data!);
        Assert.Empty((await fakes.TwoFactorAuths.GetAllAsync()).Data!);
    }

    [UnitFact]
    public async Task GivenAnotherPersonsTokens_WhenAnonymising_ThenTheyAreLeftAlone()
    {
        var fakes = Fakes.New();
        var due = await SeedPersonAsync(fakes, DateTime.UtcNow.AddDays(-200), (int)DeletionKinds.Administrative);
        var live = await SeedPersonAsync(fakes, deletedAt: null, kind: null, isDeleted: false);

        await fakes.PasswordResetTokens.CreateAsync(new PasswordResetToken
        {
            PersonId = due.Id, TokenHash = "a", ExpiresAt = DateTime.UtcNow.AddDays(1)
        });
        await fakes.PasswordResetTokens.CreateAsync(new PasswordResetToken
        {
            PersonId = live.Id, TokenHash = "b", ExpiresAt = DateTime.UtcNow.AddDays(1)
        });

        await fakes.Handler().HandleAsync(new AnonymiseExpiredDeletionsCommand());

        var remaining = (await fakes.PasswordResetTokens.GetAllAsync()).Data!.ToList();

        Assert.Single(remaining);
        Assert.Equal(live.Id, remaining[0].PersonId);
    }

    [UnitFact]
    public async Task GivenADueGoogleUser_WhenAnonymising_ThenItIsAnonymisedToo()
    {
        var fakes = Fakes.New();
        var googleUser = await SeedGoogleUserAsync(
            fakes, DateTime.UtcNow.AddDays(-200), (int)DeletionKinds.Administrative);
        var googleId = googleUser.GoogleId;

        var output = await fakes.Handler().HandleAsync(new AnonymiseExpiredDeletionsCommand());

        Assert.Equal(1, output.Data!.GoogleUsersAnonymised);
        Assert.NotEqual(googleId, googleUser.GoogleId);
        Assert.Null(googleUser.ProfilePictureUrl);
    }

    [UnitFact]
    public async Task GivenMoreRecordsThanTheBatchSize_WhenAnonymising_ThenTheRunIsBounded()
    {
        var fakes = Fakes.New();

        for (var i = 0; i < 5; i++)
        {
            await SeedPersonAsync(
                fakes, DateTime.UtcNow.AddDays(-200 + i), (int)DeletionKinds.Administrative);
        }

        var handler = fakes.Handler(Retention(batchSize: 2));

        var first = await handler.HandleAsync(new AnonymiseExpiredDeletionsCommand());

        Assert.Equal(2, first.Data!.PersonsAnonymised);

        await handler.HandleAsync(new AnonymiseExpiredDeletionsCommand());
        var third = await handler.HandleAsync(new AnonymiseExpiredDeletionsCommand());

        Assert.Equal(1, third.Data!.PersonsAnonymised);
        Assert.All(
            (await fakes.Persons.GetAllAsync()).Data!,
            person => Assert.NotNull(person.AnonymisedAt));
    }


    [UnitFact]
    public async Task GivenAStoredDeadlineAlreadyPassed_WhenAnonymising_ThenItWinsOverTheConfiguredWindow()
    {
        // UC-42 computes and stores the deadline when the request attaches. A later change to the
        // configured window must not move an obligation already owed, so the stored value decides.
        var fakes = Fakes.New();
        var person = await SeedPersonAsync(
            fakes, DateTime.UtcNow.AddDays(-2), (int)DeletionKinds.SubjectRequested,
            erasureRequestedAt: DateTime.UtcNow.AddDays(-2),
            erasureDueAt: DateTime.UtcNow.AddMinutes(-1));

        var output = await fakes.Handler().HandleAsync(new AnonymiseExpiredDeletionsCommand());

        // Deleted only two days ago, so neither configured window has elapsed — the stored deadline
        // is the only thing that makes it due
        Assert.Equal(1, output.Data!.PersonsAnonymised);
        Assert.Equal(IdentityAnonymiser.AnonymisedName, person.Name);
    }

    [UnitFact]
    public async Task GivenAStoredDeadlineStillAhead_WhenAnonymising_ThenTheRecordIsKept()
    {
        var fakes = Fakes.New();
        var person = await SeedPersonAsync(
            fakes, DateTime.UtcNow.AddDays(-200), (int)DeletionKinds.SubjectRequested,
            erasureRequestedAt: DateTime.UtcNow.AddDays(-1),
            erasureDueAt: DateTime.UtcNow.AddDays(29));

        var output = await fakes.Handler().HandleAsync(new AnonymiseExpiredDeletionsCommand());

        // Deleted 200 days ago, which both configured windows would call due — the stored deadline
        // overrides in this direction too
        Assert.Equal(0, output.Data!.TotalAnonymised);
        Assert.Null(person.AnonymisedAt);
    }

    [UnitFact]
    public async Task GivenABlockedRequestThatIsNowClear_WhenAnonymising_ThenItIsSuspendedFromTheRequestDate()
    {
        // NFR-12 blocked the request when it was made; a co-owner has since been added (UC-21). The
        // subject asked once, and the deadline has been running the whole time.
        var fakes = Fakes.New();
        var requestedAt = DateTime.UtcNow.AddDays(-60);
        var blocked = await SeedPersonAsync(
            fakes, deletedAt: null, kind: null, isDeleted: false,
            erasureRequestedAt: requestedAt,
            erasureDueAt: requestedAt.AddDays(30),
            erasureBlockedReason: ErasureMessages.BlockedByLastScopeOwnership,
            role: Roles.ScopeAdmin,
            ownedScopeIds: 7);

        // The co-owner that clears the block
        await SeedPersonAsync(
            fakes, deletedAt: null, kind: null, isDeleted: false, role: Roles.ScopeAdmin, ownedScopeIds: 7);

        var output = await fakes.Handler().HandleAsync(new AnonymiseExpiredDeletionsCommand());

        // Suspended, dated from the request rather than from now — dating it from now would hand
        // back the whole deadline for a request already 60 days outstanding
        Assert.True(blocked.IsDeleted);
        Assert.Equal(requestedAt, blocked.DeletedAt);
        Assert.Equal((int)DeletionKinds.SubjectRequested, blocked.DeletionKind);
        Assert.Null(blocked.ErasureBlockedReason);

        // And already overdue, so the same run carries it out
        Assert.Equal(1, output.Data!.PersonsAnonymised);
    }

    [UnitFact]
    public async Task GivenABlockedRequestStillBlocked_WhenAnonymising_ThenItIsLeftAlone()
    {
        var fakes = Fakes.New();
        var requestedAt = DateTime.UtcNow.AddDays(-60);
        var blocked = await SeedPersonAsync(
            fakes, deletedAt: null, kind: null, isDeleted: false,
            erasureRequestedAt: requestedAt,
            erasureDueAt: requestedAt.AddDays(30),
            erasureBlockedReason: ErasureMessages.BlockedByLastScopeOwnership,
            role: Roles.ScopeAdmin,
            ownedScopeIds: 7);

        var output = await fakes.Handler().HandleAsync(new AnonymiseExpiredDeletionsCommand());

        // Still the last owner: suspending them would leave the scope ownerless, which NFR-12
        // forbids however overdue the request is
        Assert.False(blocked.IsDeleted);
        Assert.Equal(ErasureMessages.BlockedByLastScopeOwnership, blocked.ErasureBlockedReason);
        Assert.Equal(0, output.Data!.TotalAnonymised);
        Assert.Equal("Ada Lovelace", blocked.Name);
    }

    [UnitFact]
    public async Task GivenNothingDue_WhenAnonymising_ThenTheRunSucceedsWithNoCounts()
    {
        var fakes = Fakes.New();

        var output = await fakes.Handler().HandleAsync(new AnonymiseExpiredDeletionsCommand());

        Assert.True(output.Success);
        Assert.Empty(output.Errors);
        Assert.Equal(0, output.Data!.TotalAnonymised);
    }
}
