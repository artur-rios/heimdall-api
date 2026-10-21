using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Heimdall.Command.Auditing;
using ArturRios.Heimdall.Command.Handlers;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Command.Services;
using ArturRios.Heimdall.Command.Tests.Support;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Domain.Persistence;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Heimdall.Shared.Retention;
using ArturRios.Output;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Mock;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

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

    private static DataRetentionOptions Retention(
        int batchSize = 500, int maxBatches = DataRetentionOptions.DefaultMaxBatchesPerTick) => new()
    {
        SubjectErasureDeadline = ErasureDeadline,
        AdministrativeDeletionWindow = AdministrativeWindow,
        PurgeBatchSize = batchSize,
        MaxBatchesPerTick = maxBatches
    };

    private sealed record Fakes(
        AsyncFakeRepository<Person, long> Persons,
        AsyncFakeRepository<GoogleUser, long> GoogleUsers,
        AsyncFakeRepository<PasswordResetToken, long> PasswordResetTokens,
        AsyncFakeRepository<EmailVerificationToken, long> EmailVerificationTokens,
        AsyncFakeRepository<TwoFactorAuth, long> TwoFactorAuths,
        AsyncFakeRepository<ScopeOwnerInvitation, long> Invitations,
        RecordingAuditLogWriter AuditLog)
    {
        public static Fakes New() => new(new(), new(), new(), new(), new(), new(), new());

        public AnonymiseExpiredDeletionsCommandHandler Handler(
            DataRetentionOptions? retention = null, IAtomicWrites? atomicWrites = null,
            IAsyncRepository<GoogleUser, long>? googleUserWriter = null, IAuditLogWriter? auditLog = null,
            IAsyncReadOnlyRepository<Person, long>? personReader = null) =>
            new(personReader ?? Persons, GoogleUsers, googleUserWriter ?? GoogleUsers,
                PasswordResetTokens,
                EmailVerificationTokens,
                TwoFactorAuths,
                retention ?? Retention(),
                atomicWrites ?? InMemoryAtomicWrites.Instance,
                new InMemoryScopeOwnerInvitations(Invitations, Persons, new AsyncFakeRepository<Scope, long>()),
                Invitations,
                auditLog ?? AuditLog,
                NullLogger<AnonymiseExpiredDeletionsCommandHandler>.Instance);

        /// <summary>The handler's own <c>ScopeOwnerInvitationVoided</c> entries.</summary>
        public List<(string Action, Guid? TargetId, bool Succeeded)> Voids() =>
            AuditLog.Entries.Where(entry => entry.Action == ScopeOwnerInvitationAuditActions.Voided).ToList();
    }

    private static async Task<ScopeOwnerInvitation> SeedInvitationAsync(
        Fakes fakes, long inviteeId, long? inviterId, DateTime? endedAt = null)
    {
        var invitation = new ScopeOwnerInvitation
        {
            PublicId = Guid.NewGuid(), ScopeId = 1, InviteePersonId = inviteeId, InvitedByPersonId = inviterId,
            CreatedAt = DateTime.UtcNow.AddDays(-1), ExpiresAt = DateTime.UtcNow.AddDays(6), EndedAt = endedAt,
            Outcome = endedAt is null ? null : ScopeOwnerInvitationOutcomes.Declined
        };
        await fakes.Invitations.CreateAsync(invitation);
        return invitation;
    }

    private static Task<Person> SeedDueScopeAdminAsync(Fakes fakes) =>
        SeedPersonAsync(
            fakes, DateTime.UtcNow - ErasureDeadline - TimeSpan.FromDays(1), (int)DeletionKinds.SubjectRequested,
            role: Roles.ScopeAdmin);

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
    public async Task GivenARestrictedRecordPastItsWindow_WhenAnonymising_ThenItIsPreservedAsItStands()
    {
        // NFR-24: restriction and deletion are independent, and a restricted record is preserved
        // exactly as it stands rather than entering the anonymisation window — here, an overdue
        // administrative deletion of a person and of a Google User, each under a restriction.
        var fakes = Fakes.New();
        var person = await SeedPersonAsync(
            fakes, DateTime.UtcNow - AdministrativeWindow - TimeSpan.FromDays(1),
            (int)DeletionKinds.Administrative);
        person.ProcessingRestrictedAt = DateTime.UtcNow.AddDays(-5);
        var googleUser = await SeedGoogleUserAsync(
            fakes, DateTime.UtcNow - AdministrativeWindow - TimeSpan.FromDays(1),
            (int)DeletionKinds.Administrative);
        googleUser.ProcessingRestrictedAt = DateTime.UtcNow.AddDays(-5);

        var output = await fakes.Handler().HandleAsync(new AnonymiseExpiredDeletionsCommand());

        Assert.True(output.Success);
        Assert.Equal(0, output.Data!.TotalAnonymised);
        Assert.Equal("Ada Lovelace", person.Name);
        Assert.Null(person.AnonymisedAt);
        Assert.Equal("Ada Lovelace", googleUser.Name);
        Assert.Null(googleUser.AnonymisedAt);
    }

    [UnitFact]
    public async Task GivenABlockedErasureOfARestrictedPerson_WhenTheBlockClears_ThenItIsNotSuspended()
    {
        // The subject asked for erasure, was blocked as a scope's last owner (NFR-12), and has since
        // restricted processing (UC-44 allows it). The block no longer holding is not enough to move
        // a restricted record toward erasure.
        var fakes = Fakes.New();
        var person = await SeedPersonAsync(
            fakes, deletedAt: null, kind: null, isDeleted: false,
            erasureRequestedAt: DateTime.UtcNow.AddDays(-40),
            erasureBlockedReason: "last owner");
        person.ProcessingRestrictedAt = DateTime.UtcNow.AddDays(-5);

        var output = await fakes.Handler().HandleAsync(new AnonymiseExpiredDeletionsCommand());

        Assert.True(output.Success);
        Assert.False(person.IsDeleted);
        Assert.Null(person.AnonymisedAt);
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

        var handler = fakes.Handler(Retention(batchSize: 2, maxBatches: 1));

        var first = await handler.HandleAsync(new AnonymiseExpiredDeletionsCommand());

        Assert.Equal(2, first.Data!.PersonsAnonymised);
        Assert.Equal(3, first.Data.Backlog);

        await handler.HandleAsync(new AnonymiseExpiredDeletionsCommand());
        var third = await handler.HandleAsync(new AnonymiseExpiredDeletionsCommand());

        Assert.Equal(1, third.Data!.PersonsAnonymised);
        Assert.Equal(0, third.Data.Backlog);
        Assert.All(
            (await fakes.Persons.GetAllAsync()).Data!,
            person => Assert.NotNull(person.AnonymisedAt));
    }

    // #206: a run repeats its batch while the batch comes back full, up to MaxBatchesPerTick.

    [UnitFact]
    public async Task GivenABacklogOfThreeFullBatches_WhenAnonymising_ThenItDrainsInOneRunAndTheLedgerNamesEveryone()
    {
        // Given six overdue persons and six overdue Google Users, and a batch of two
        var fakes = Fakes.New();
        var due = DateTime.UtcNow - AdministrativeWindow - TimeSpan.FromDays(1);
        var ids = new List<Guid>();

        for (var i = 0; i < 6; i++)
        {
            ids.Add((await SeedPersonAsync(fakes, due.AddDays(-i), (int)DeletionKinds.Administrative)).PublicId);
            ids.Add((await SeedGoogleUserAsync(fakes, due.AddDays(-i), (int)DeletionKinds.Administrative)).PublicId);
        }

        // When
        var output = await fakes.Handler(Retention(batchSize: 2)).HandleAsync(new AnonymiseExpiredDeletionsCommand());

        // Then
        Assert.True(output.Success);
        Assert.Equal(6, output.Data!.PersonsAnonymised);
        Assert.Equal(6, output.Data.GoogleUsersAnonymised);
        Assert.Equal(0, output.Data.Backlog);
        Assert.Equal(ids.Order(), output.Data.AnonymisedIds.Order());
        Assert.All((await fakes.Persons.GetAllAsync()).Data!, person => Assert.NotNull(person.AnonymisedAt));
        Assert.All((await fakes.GoogleUsers.GetAllAsync()).Data!, googleUser => Assert.NotNull(googleUser.AnonymisedAt));
    }

    [UnitFact]
    public async Task GivenMoreFullBatchesThanTheCap_WhenAnonymising_ThenTheRunStopsAtTheCapAndReportsWhatIsLeft()
    {
        // Given seven overdue persons, a batch of two and at most two batches a run
        var fakes = Fakes.New();
        var persons = new List<Person>();

        for (var i = 0; i < 7; i++)
        {
            persons.Add(await SeedPersonAsync(
                fakes, DateTime.UtcNow.AddDays(-200 + i), (int)DeletionKinds.Administrative));
        }

        var handler = fakes.Handler(Retention(batchSize: 2, maxBatches: 2));

        // When
        var first = await handler.HandleAsync(new AnonymiseExpiredDeletionsCommand());

        // Then — the four longest deleted go, and the three left are the backlog
        Assert.Equal(4, first.Data!.PersonsAnonymised);
        Assert.Equal(3, first.Data.Backlog);
        Assert.All(persons.Take(4), person => Assert.NotNull(person.AnonymisedAt));
        Assert.All(persons.Skip(4), person => Assert.Null(person.AnonymisedAt));

        // And the next run finishes it
        var second = await handler.HandleAsync(new AnonymiseExpiredDeletionsCommand());

        Assert.Equal(3, second.Data!.PersonsAnonymised);
        Assert.Equal(0, second.Data.Backlog);
    }

    [UnitFact]
    public async Task GivenAFullBatchInWhichNobodyCanBeAnonymised_WhenAnonymising_ThenTheBatchIsNotTakenAgain()
    {
        // Given four overdue persons, a batch of two, and writes that refuse every one of them without
        // changing the row — so each would be selected again
        var fakes = Fakes.New();

        for (var i = 0; i < 4; i++)
        {
            await SeedPersonAsync(fakes, DateTime.UtcNow.AddDays(-200 + i), (int)DeletionKinds.Administrative);
        }

        var atomicWrites = new Mock<IAtomicWrites>();
        atomicWrites
            .Setup(writes => writes.TryAnonymiseAsync(
                It.IsAny<Person>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime>()))
            .ReturnsAsync(false);

        // When
        var output = await fakes.Handler(Retention(batchSize: 2), atomicWrites: atomicWrites.Object)
            .HandleAsync(new AnonymiseExpiredDeletionsCommand());

        // Then — one batch of two attempts, not the cap's worth over the same two, and the four still due
        // are the backlog
        Assert.True(output.Success);
        atomicWrites.Verify(
            writes => writes.TryAnonymiseAsync(
                It.IsAny<Person>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime>()),
            Times.Exactly(2));
        Assert.Equal(0, output.Data!.PersonsAnonymised);
        Assert.Equal(4, output.Data.Backlog);
    }

    [UnitFact]
    public async Task GivenTheBacklogCountFailsAfterASuccessfulRun_WhenAnonymising_ThenTheRunSucceedsWithTheBacklogUnknown()
    {
        // Given two overdue persons, one batch of two allowed — so the run ends on a full batch and counts —
        // and a person store that fails every query once the anonymising has begun
        var fakes = Fakes.New();
        var persons = new List<Person>();

        for (var i = 0; i < 2; i++)
        {
            persons.Add(await SeedPersonAsync(
                fakes, DateTime.UtcNow.AddDays(-200 + i), (int)DeletionKinds.Administrative));
        }

        var failing = false;
        var reader = new Mock<IAsyncReadOnlyRepository<Person, long>>();
        reader.Setup(r => r.Query()).Returns(() =>
            failing ? throw new InvalidOperationException("the connection was lost") : fakes.Persons.Query());
        var atomicWrites = new InMemoryAtomicWrites { BeforeConditionalWrite = _ => failing = true };

        // When
        var output = await fakes
            .Handler(Retention(batchSize: 2, maxBatches: 1), atomicWrites: atomicWrites, personReader: reader.Object)
            .HandleAsync(new AnonymiseExpiredDeletionsCommand());

        // Then — the erasures went through and the run is a success; only the backlog is unknown
        Assert.True(output.Success, string.Join("; ", output.Errors));
        Assert.DoesNotContain(RetentionMessages.AnonymisationInterrupted, output.Errors);
        Assert.Equal(2, output.Data!.PersonsAnonymised);
        Assert.Equal(persons.Select(person => person.PublicId).Order(), output.Data.AnonymisedIds.Order());
        Assert.Null(output.Data.Backlog);
    }

    [UnitFact]
    public async Task GivenAWriteThrowsInALaterBatch_WhenAnonymising_ThenTheRunReportsItAndTheLedgerNamesThoseAlreadyAnonymised()
    {
        // Given four overdue persons, a batch of two, and a write that throws on the fourth
        var fakes = Fakes.New();
        var persons = new List<Person>();

        for (var i = 0; i < 4; i++)
        {
            persons.Add(await SeedPersonAsync(
                fakes, DateTime.UtcNow.AddDays(-200 + i), (int)DeletionKinds.Administrative));
        }

        var atomicWrites = new InMemoryAtomicWrites
        {
            BeforeConditionalWrite = person =>
            {
                if (person == persons[3])
                {
                    throw new InvalidOperationException("the connection was lost");
                }
            }
        };

        // When
        var output = await fakes.Handler(Retention(batchSize: 2), atomicWrites: atomicWrites)
            .HandleAsync(new AnonymiseExpiredDeletionsCommand());

        // Then — reported rather than thrown, and the three erased before it are in the ledger
        Assert.False(output.Success);
        Assert.Contains(RetentionMessages.AnonymisationInterrupted, output.Errors);
        Assert.Equal(3, output.Data!.PersonsAnonymised);
        Assert.Equal(persons.Take(3).Select(person => person.PublicId).Order(), output.Data.AnonymisedIds.Order());
    }

    [UnitFact]
    public async Task GivenTheGoogleUserSaveFailsAfterSeveralBatchesOfPersons_WhenAnonymising_ThenTheLedgerNamesEveryPerson()
    {
        // Given five overdue persons — three batches of two — and an overdue Google User whose save fails
        var fakes = Fakes.New();
        var persons = new List<Person>();

        for (var i = 0; i < 5; i++)
        {
            persons.Add(await SeedPersonAsync(
                fakes, DateTime.UtcNow.AddDays(-200 + i), (int)DeletionKinds.Administrative));
        }

        var googleUser = await SeedGoogleUserAsync(
            fakes, DateTime.UtcNow - AdministrativeWindow - TimeSpan.FromDays(1), (int)DeletionKinds.Administrative);
        var failingWriter = new Mock<IAsyncRepository<GoogleUser, long>>();
        failingWriter
            .Setup(writer => writer.UpdateRangeAsync(It.IsAny<IEnumerable<GoogleUser>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DataOutput<IEnumerable<GoogleUser>>.New.WithError("database unavailable"));

        // When
        var output = await fakes.Handler(Retention(batchSize: 2), googleUserWriter: failingWriter.Object)
            .HandleAsync(new AnonymiseExpiredDeletionsCommand());

        // Then — every person anonymised in the run is in the ledger, from every batch, and the Google User
        // is not
        Assert.False(output.Success);
        Assert.Equal(5, output.Data!.PersonsAnonymised);
        Assert.Equal(persons.Select(person => person.PublicId).Order(), output.Data.AnonymisedIds.Order());
        Assert.DoesNotContain(googleUser.PublicId, output.Data.AnonymisedIds);
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
    public async Task GivenABlockedRequestThatCannotBeSaved_WhenAnonymising_ThenTheDueIdentityIsStillAnonymised()
    {
        // #210: one request the retry cannot save used to return before any due identity was selected,
        // so every following run did nothing and statutory deadlines passed unmet.
        var fakes = Fakes.New();
        var requestedAt = DateTime.UtcNow.AddDays(-60);
        var failing = await SeedPersonAsync(
            fakes, deletedAt: null, kind: null, isDeleted: false,
            erasureRequestedAt: requestedAt,
            erasureBlockedReason: ErasureMessages.BlockedByLastScopeOwnership);
        var due = await SeedDueScopeAdminAsync(fakes);

        var atomicWrites = new InMemoryAtomicWrites
        {
            BeforeConditionalWrite = person =>
            {
                if (person == failing)
                {
                    throw new InvalidOperationException("a constraint rejects this row");
                }
            }
        };

        var output = await fakes.Handler(atomicWrites: atomicWrites)
            .HandleAsync(new AnonymiseExpiredDeletionsCommand());

        Assert.NotNull(due.AnonymisedAt);
        Assert.Equal(1, output.Data!.PersonsAnonymised);
        Assert.Contains(due.PublicId, output.Data.AnonymisedIds);

        // Reported, not swallowed: the run is not a success
        Assert.False(output.Success);
        Assert.Contains(RetentionMessages.BlockedErasureRetryFailed, output.Errors);
    }

    [UnitFact]
    public async Task GivenMoreBlockedRequestsThanABatch_WhenAnonymising_ThenOnlyTheOldestBatchIsRetried()
    {
        var fakes = Fakes.New();
        var oldest = await SeedBlockedAsync(fakes, daysAgo: 90);
        var middle = await SeedBlockedAsync(fakes, daysAgo: 60);
        var newest = await SeedBlockedAsync(fakes, daysAgo: 30);

        await fakes.Handler(Retention(batchSize: 2, maxBatches: 1)).HandleAsync(new AnonymiseExpiredDeletionsCommand());

        Assert.True(oldest.IsDeleted);
        Assert.True(middle.IsDeleted);
        Assert.False(newest.IsDeleted);
    }

    [UnitFact]
    public async Task GivenAFullBatchOfRequestsStillBlocked_WhenAnonymising_ThenANewerRequestIsStillRetried()
    {
        // Given the two oldest requests still blocked — each the last owner of a scope — and a newer one
        // whose block has cleared, with a batch of two
        var fakes = Fakes.New();
        var stillBlocked = new[]
        {
            await SeedStillBlockedAsync(fakes, daysAgo: 90, scopeId: 71),
            await SeedStillBlockedAsync(fakes, daysAgo: 60, scopeId: 72)
        };
        var newest = await SeedBlockedAsync(fakes, daysAgo: 30);

        // When
        await fakes.Handler(Retention(batchSize: 2)).HandleAsync(new AnonymiseExpiredDeletionsCommand());

        // Then — the batch of blocked requests did not keep the newer one waiting
        Assert.All(stillBlocked, person => Assert.False(person.IsDeleted));
        Assert.True(newest.IsDeleted);
    }

    [UnitFact]
    public async Task GivenAFullBatchOfRetriesThatKeepFailing_WhenAnonymising_ThenEachIsTriedOnceAndANewerOneIsRetried()
    {
        // Given the two oldest requests failing on every attempt, a newer one that can go, and a batch of two
        var fakes = Fakes.New();
        var failing = new[] { await SeedBlockedAsync(fakes, daysAgo: 90), await SeedBlockedAsync(fakes, daysAgo: 60) };
        var newest = await SeedBlockedAsync(fakes, daysAgo: 30);
        var attempts = new Dictionary<Person, int>();
        var atomicWrites = new InMemoryAtomicWrites
        {
            BeforeConditionalWrite = person =>
            {
                attempts[person] = attempts.GetValueOrDefault(person) + 1;

                if (failing.Contains(person))
                {
                    throw new InvalidOperationException("a constraint rejects this row");
                }
            }
        };

        // When
        var output = await fakes.Handler(Retention(batchSize: 2), atomicWrites: atomicWrites)
            .HandleAsync(new AnonymiseExpiredDeletionsCommand());

        // Then — no spin on the failing pair, and the newer request was reached
        Assert.All(failing, person => Assert.Equal(1, attempts[person]));
        Assert.True(newest.IsDeleted);
        Assert.Contains(RetentionMessages.BlockedErasureRetryFailed, output.Errors);
    }

    private static Task<Person> SeedStillBlockedAsync(Fakes fakes, int daysAgo, long scopeId) =>
        SeedPersonAsync(
            fakes, deletedAt: null, kind: null, isDeleted: false,
            erasureRequestedAt: DateTime.UtcNow.AddDays(-daysAgo),
            erasureDueAt: DateTime.UtcNow.AddDays(-daysAgo + 30),
            erasureBlockedReason: ErasureMessages.BlockedByLastScopeOwnership,
            role: Roles.ScopeAdmin,
            ownedScopeIds: scopeId);

    [UnitFact]
    public async Task GivenTheGoogleUserSaveFails_WhenAnonymising_ThenTheAnonymisedPersonsAreStillNamed()
    {
        // The persons are anonymised before the Google Users are saved; their ids are the erasure ledger
        // and must survive the failed save, while the unsaved Google User must not appear in it.
        var fakes = Fakes.New();
        var person = await SeedDueScopeAdminAsync(fakes);
        var googleUser = await SeedGoogleUserAsync(
            fakes, DateTime.UtcNow - AdministrativeWindow - TimeSpan.FromDays(1), (int)DeletionKinds.Administrative);
        var failingWriter = new Mock<IAsyncRepository<GoogleUser, long>>();
        failingWriter
            .Setup(writer => writer.UpdateRangeAsync(It.IsAny<IEnumerable<GoogleUser>>(), It.IsAny<CancellationToken>()))
            .Returns(async (IEnumerable<GoogleUser> users, CancellationToken token) =>
                (await fakes.GoogleUsers.UpdateRangeAsync(users, token)).WithError("database unavailable"));

        var output = await fakes.Handler(googleUserWriter: failingWriter.Object)
            .HandleAsync(new AnonymiseExpiredDeletionsCommand());

        Assert.False(output.Success);
        Assert.Equal(1, output.Data!.PersonsAnonymised);
        Assert.Equal(0, output.Data.GoogleUsersAnonymised);
        Assert.Equal([person.PublicId], output.Data.AnonymisedIds);
        Assert.DoesNotContain(googleUser.PublicId, output.Data.AnonymisedIds);
    }

    [UnitFact]
    public async Task GivenTheBlockedRequestsCannotBeLoaded_WhenAnonymising_ThenTheDueIdentityIsStillAnonymised()
    {
        var fakes = Fakes.New();
        var due = await SeedDueScopeAdminAsync(fakes);
        var calls = 0;
        var reader = new Mock<IAsyncReadOnlyRepository<Person, long>>();
        reader.Setup(r => r.Query()).Returns(() =>
            ++calls == 1 ? throw new InvalidOperationException("query failed") : fakes.Persons.Query());

        var output = await fakes.Handler(personReader: reader.Object)
            .HandleAsync(new AnonymiseExpiredDeletionsCommand());

        Assert.NotNull(due.AnonymisedAt);
        Assert.False(output.Success);
        Assert.Contains(RetentionMessages.BlockedErasureRetryFailed, output.Errors);
    }

    [UnitFact]
    public async Task GivenARetryFailureAndNothingDue_WhenAnonymising_ThenTheErrorIsStillReported()
    {
        var fakes = Fakes.New();
        var failing = await SeedBlockedAsync(fakes, daysAgo: 5);
        var atomicWrites = new InMemoryAtomicWrites
        {
            BeforeConditionalWrite = person =>
            {
                if (person == failing)
                {
                    throw new InvalidOperationException("a constraint rejects this row");
                }
            }
        };

        var output = await fakes.Handler(atomicWrites: atomicWrites)
            .HandleAsync(new AnonymiseExpiredDeletionsCommand());

        Assert.False(output.Success);
        Assert.Contains(RetentionMessages.BlockedErasureRetryFailed, output.Errors);
        Assert.Equal(0, output.Data!.TotalAnonymised);
    }

    private static Task<Person> SeedBlockedAsync(Fakes fakes, int daysAgo) =>
        SeedPersonAsync(
            fakes, deletedAt: null, kind: null, isDeleted: false,
            erasureRequestedAt: DateTime.UtcNow.AddDays(-daysAgo),
            erasureDueAt: DateTime.UtcNow.AddDays(-daysAgo + 30),
            erasureBlockedReason: ErasureMessages.BlockedByLastScopeOwnership);

    [UnitFact]
    public async Task GivenNothingDue_WhenAnonymising_ThenTheRunSucceedsWithNoCounts()
    {
        var fakes = Fakes.New();

        var output = await fakes.Handler().HandleAsync(new AnonymiseExpiredDeletionsCommand());

        Assert.True(output.Success);
        Assert.Empty(output.Errors);
        Assert.Equal(0, output.Data!.TotalAnonymised);
    }

    [UnitFact]
    public async Task GivenADuePersonWithFactorBudgetsSpent_WhenAnonymising_ThenTheCountersAreCleared()
    {
        // NFR-20: behavioural counters describe the person as surely as their name does
        var fakes = Fakes.New();
        var person = await SeedPersonAsync(
            fakes, DateTime.UtcNow - AdministrativeWindow - TimeSpan.FromDays(1),
            (int)DeletionKinds.Administrative);
        person.FailedSecondFactorAttempts = 4;
        person.SecondFactorLockedOutUntil = DateTime.UtcNow.AddHours(1);
        person.SecondFactorLockouts = 2;
        person.FailedReauthenticationAttempts = 3;
        person.ReauthenticationWindowStartedAt = DateTime.UtcNow.AddMinutes(-5);
        person.ReauthenticationPausedUntil = DateTime.UtcNow.AddMinutes(5);
        person.AccountEmailsSent = 4;
        person.AccountEmailWindowEndsAt = DateTime.UtcNow.AddHours(5);
        person.AccountEmailLastSentAt = DateTime.UtcNow.AddMinutes(-1);

        var output = await fakes.Handler().HandleAsync(new AnonymiseExpiredDeletionsCommand());

        Assert.True(output.Success);
        Assert.Equal(0, person.FailedSecondFactorAttempts);
        Assert.Null(person.SecondFactorLockedOutUntil);
        Assert.Equal(0, person.SecondFactorLockouts);
        Assert.Equal(0, person.FailedReauthenticationAttempts);
        Assert.Null(person.ReauthenticationWindowStartedAt);
        Assert.Null(person.ReauthenticationPausedUntil);
        Assert.Equal(0, person.AccountEmailsSent);
        Assert.Null(person.AccountEmailWindowEndsAt);
        Assert.Null(person.AccountEmailLastSentAt);
    }

    [UnitFact]
    public async Task GivenAnErasedInviteeWithAnOpenInvitation_WhenAnonymising_ThenItIsVoidedAndAudited()
    {
        // Given — erasure voids the invitation (FR-SC-17)
        var fakes = Fakes.New();
        var person = await SeedDueScopeAdminAsync(fakes);
        var invitation = await SeedInvitationAsync(fakes, person.Id, null);

        // When
        var output = await fakes.Handler().HandleAsync(new AnonymiseExpiredDeletionsCommand());

        // Then
        Assert.True(output.Success);
        Assert.Equal(ScopeOwnerInvitationOutcomes.Voided, invitation.Outcome);
        Assert.NotNull(invitation.EndedAt);
        Assert.Equal(1, output.Data!.ScopeOwnerInvitationsVoided);
        Assert.Equal([(ScopeOwnerInvitationAuditActions.Voided, (Guid?)invitation.PublicId, true)], fakes.AuditLog.Entries);
    }

    [UnitFact]
    public async Task GivenAnErasedInviteeWithSeveralInvitations_WhenAnonymising_ThenEachUnendedOneIsVoidedOnItsOwnId()
    {
        // Given — two unended invitations and one the invitee already declined
        var fakes = Fakes.New();
        var person = await SeedDueScopeAdminAsync(fakes);
        var first = await SeedInvitationAsync(fakes, person.Id, null);
        var second = await SeedInvitationAsync(fakes, person.Id, null);
        var declined = await SeedInvitationAsync(fakes, person.Id, null, endedAt: DateTime.UtcNow.AddHours(-1));

        // When
        var output = await fakes.Handler().HandleAsync(new AnonymiseExpiredDeletionsCommand());

        // Then — one void entry per voided invitation, each on that invitation, in no promised order
        Assert.True(output.Success);
        Assert.Equal(2, output.Data!.ScopeOwnerInvitationsVoided);
        Assert.Equal(ScopeOwnerInvitationOutcomes.Declined, declined.Outcome);
        Assert.Equal(
            new[] { first.PublicId, second.PublicId }.Order(),
            fakes.Voids().Select(entry => entry.TargetId!.Value).Order());
        Assert.Equal(2, fakes.AuditLog.Entries.Count);
    }

    [UnitFact]
    public async Task GivenAnErasedInviter_WhenAnonymising_ThenTheirInvitationStaysOpenWithoutAnInviter()
    {
        // Given
        var fakes = Fakes.New();
        var inviter = await SeedDueScopeAdminAsync(fakes);
        var invitation = await SeedInvitationAsync(fakes, inviteeId: 999, inviter.Id);

        // When
        var output = await fakes.Handler().HandleAsync(new AnonymiseExpiredDeletionsCommand());

        // Then
        Assert.True(output.Success);
        Assert.Null(invitation.InvitedByPersonId);
        Assert.Null(invitation.EndedAt);
        Assert.Equal(0, output.Data!.ScopeOwnerInvitationsVoided);
        Assert.Empty(fakes.AuditLog.Entries);
    }

    [UnitFact]
    public async Task GivenAPersonRestrictedSinceSelection_WhenAnonymising_ThenTheirInvitationsAreLeftAsTheyStand()
    {
        // Given — NFR-24: a record restricted between the selection and its write is not erased, so
        // nothing that names it is touched either
        var fakes = Fakes.New();
        var person = await SeedDueScopeAdminAsync(fakes);
        var received = await SeedInvitationAsync(fakes, person.Id, null);
        var sent = await SeedInvitationAsync(fakes, inviteeId: 999, person.Id);
        var atomicWrites = new InMemoryAtomicWrites
        {
            BeforeConditionalWrite = restricted => restricted.ProcessingRestrictedAt = DateTime.UtcNow
        };

        // When
        var output = await fakes.Handler(atomicWrites: atomicWrites).HandleAsync(new AnonymiseExpiredDeletionsCommand());

        // Then
        Assert.True(output.Success);
        Assert.Null(person.AnonymisedAt);
        Assert.Null(received.EndedAt);
        Assert.Null(received.Outcome);
        Assert.Equal(person.Id, sent.InvitedByPersonId);
        Assert.Equal(0, output.Data!.ScopeOwnerInvitationsVoided);
        Assert.Empty(fakes.AuditLog.Entries);
    }

    [UnitFact]
    public async Task GivenVoidedInvitations_WhenTheAuditedPassRuns_ThenOneVoidEntryTargetsEachInvitation()
    {
        // Given — the pass as the application registers it, behind the auditing decorator (G-F7)
        var fakes = Fakes.New();
        var person = await SeedDueScopeAdminAsync(fakes);
        var first = await SeedInvitationAsync(fakes, person.Id, null);
        var second = await SeedInvitationAsync(fakes, person.Id, null);
        var writer = fakes.AuditLog;
        var handler = Audited(fakes.Handler(), writer);

        // When
        var output = await handler.HandleAsync(new AnonymiseExpiredDeletionsCommand());

        // Then — the pass's own entry, untargeted, and one targeted void per invitation
        Assert.True(output.Success);
        Assert.Contains((nameof(AnonymiseExpiredDeletionsCommand), (Guid?)null, true), writer.Entries);
        var voids = writer.Entries.Where(entry => entry.Action == ScopeOwnerInvitationAuditActions.Voided).ToList();
        Assert.Equal(new[] { first.PublicId, second.PublicId }.Order(), voids.Select(entry => entry.TargetId!.Value).Order());
        Assert.All(voids, entry => Assert.True(entry.Succeeded));
        Assert.Equal(3, writer.Entries.Count);
    }

    private static AuditingCommandHandler<AnonymiseExpiredDeletionsCommand, AnonymiseExpiredDeletionsCommandOutput> Audited(
        AnonymiseExpiredDeletionsCommandHandler handler, IAuditLogWriter auditLog) =>
        new(handler, auditLog,
            NullLogger<AuditingCommandHandler<AnonymiseExpiredDeletionsCommand, AnonymiseExpiredDeletionsCommandOutput>>
                .Instance);

    [UnitFact]
    public async Task GivenALaterWriteFailsAfterAVoid_WhenTheAuditedPassRuns_ThenTheVoidIsStillAudited()
    {
        // Given — the void commits on its own, so a Google User save failing after it must not take its
        // audit entry with it: a retry finds the invitation already ended and returns no id to audit
        var fakes = Fakes.New();
        var person = await SeedDueScopeAdminAsync(fakes);
        var invitation = await SeedInvitationAsync(fakes, person.Id, null);
        await SeedGoogleUserAsync(
            fakes, DateTime.UtcNow - AdministrativeWindow - TimeSpan.FromDays(1), (int)DeletionKinds.Administrative);
        var failingWriter = new Mock<IAsyncRepository<GoogleUser, long>>();
        failingWriter
            .Setup(writer => writer.UpdateRangeAsync(It.IsAny<IEnumerable<GoogleUser>>(), It.IsAny<CancellationToken>()))
            .Returns(async (IEnumerable<GoogleUser> users, CancellationToken token) =>
                (await fakes.GoogleUsers.UpdateRangeAsync(users, token)).WithError("database unavailable"));
        var auditLog = fakes.AuditLog;
        var handler = Audited(fakes.Handler(googleUserWriter: failingWriter.Object), auditLog);

        // When
        var output = await handler.HandleAsync(new AnonymiseExpiredDeletionsCommand());

        // Then
        Assert.False(output.Success);
        Assert.Equal(ScopeOwnerInvitationOutcomes.Voided, invitation.Outcome);
        Assert.Contains((ScopeOwnerInvitationAuditActions.Voided, (Guid?)invitation.PublicId, true), auditLog.Entries);
    }

    [UnitFact]
    public async Task GivenAnAnonymisedPersonStillNamedByInvitations_WhenAnonymising_ThenTheyAreForgottenAndAudited()
    {
        // Given — an earlier run anonymised the person and then failed before their invitations were
        // forgotten, so one is still open to them and another still names them as its inviter
        var fakes = Fakes.New();
        var person = await SeedPersonAsync(
            fakes, DateTime.UtcNow.AddDays(-60), (int)DeletionKinds.SubjectRequested,
            anonymisedAt: DateTime.UtcNow.AddDays(-1), role: Roles.ScopeAdmin);
        var received = await SeedInvitationAsync(fakes, person.Id, null);
        var sent = await SeedInvitationAsync(fakes, inviteeId: 999, person.Id, endedAt: DateTime.UtcNow.AddDays(-2));
        var auditLog = fakes.AuditLog;
        var handler = Audited(fakes.Handler(), auditLog);

        // When
        var output = await handler.HandleAsync(new AnonymiseExpiredDeletionsCommand());

        // Then
        Assert.True(output.Success);
        Assert.Equal(ScopeOwnerInvitationOutcomes.Voided, received.Outcome);
        Assert.Null(sent.InvitedByPersonId);
        Assert.Equal(1, output.Data!.ScopeOwnerInvitationsVoided);
        Assert.Equal(
            [(ScopeOwnerInvitationAuditActions.Voided, (Guid?)received.PublicId, true)],
            auditLog.Entries.Where(entry => entry.Action == ScopeOwnerInvitationAuditActions.Voided));
    }
}
