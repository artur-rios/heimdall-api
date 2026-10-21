using ArturRios.Data.Relational.Core.Repositories;
using ArturRios.Heimdall.Command.Auditing;
using ArturRios.Heimdall.Command.Handlers;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Services;
using ArturRios.Heimdall.Data.Configuration;
using ArturRios.Heimdall.Data.Persistence;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Shared.Retention;
using ArturRios.Heimdall.WebApi.Security;
using ArturRios.Heimdall.WebApi.Tests.Support;
using ArturRios.Util.Test.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArturRios.Heimdall.WebApi.Tests;

/// <summary>
///     The anonymisation pass (NFR-20) against the real database. Two things here cannot be shown
///     over in-memory fakes, and both would fail in production rather than in a unit test.
/// </summary>
/// <remarks>
///     <para>
///         The first is translation. The pass selects on a conditional — the deadline depends on the
///         record's <c>DeletionKind</c> — and a fake evaluates that in memory whatever EF would have
///         made of it.
///     </para>
///     <para>
///         The second is the unique indexes. <c>GOOGLE_USER</c> carries unconditional unique indexes
///         on <c>(scope_id, google_id)</c> and <c>(scope_id, LOWER(email))</c>, and no fake enforces
///         them, so a constant placeholder would pass every unit test and fail on the second Google
///         User anonymised in a scope. This seeds two deliberately.
///     </para>
/// </remarks>
[Collection(nameof(FunctionalCollection))]
public class IdentityAnonymisationTests(PostgresFixture fixture)
{
    private static readonly DataRetentionOptions Retention = new()
    {
        SubjectErasureDeadline = TimeSpan.FromDays(30),
        AdministrativeDeletionWindow = TimeSpan.FromDays(90),
        PurgeBatchSize = 500
    };

    internal static AnonymiseExpiredDeletionsCommandHandler Handler(
        AppDbContext context, DataRetentionOptions? retention = null)
    {
        var persons = new EfRepository<Person, long>(context);
        var googleUsers = new EfRepository<GoogleUser, long>(context);
        var passwordResetTokens = new EfRepository<PasswordResetToken, long>(context);
        var emailVerificationTokens = new EfRepository<EmailVerificationToken, long>(context);
        var twoFactorAuths = new EfRepository<TwoFactorAuth, long>(context);

        return new AnonymiseExpiredDeletionsCommandHandler(
            persons, googleUsers, googleUsers,
            passwordResetTokens,
            emailVerificationTokens,
            twoFactorAuths,
            retention ?? Retention,
            new AtomicWrites(context),
            new ScopeOwnerInvitations(context),
            new EfRepository<ScopeOwnerInvitation, long>(context),
            AuditLog(context),
            NullLogger<AnonymiseExpiredDeletionsCommandHandler>.Instance);
    }

    /// <summary>The real audit writer, actorless as the scheduled pass is.</summary>
    private static AuditLogWriter AuditLog(AppDbContext context) =>
        new(new EfRepository<AuditLog, long>(context), new HttpContextActorAccessor(new HttpContextAccessor()));

    private static ReapplyErasuresCommandHandler ReapplyHandler(AppDbContext context)
    {
        var persons = new EfRepository<Person, long>(context);
        var googleUsers = new EfRepository<GoogleUser, long>(context);

        return new ReapplyErasuresCommandHandler(
            persons, googleUsers, googleUsers,
            new EfRepository<PasswordResetToken, long>(context),
            new EfRepository<EmailVerificationToken, long>(context),
            new EfRepository<TwoFactorAuth, long>(context),
            new AtomicWrites(context),
            new ScopeOwnerInvitations(context),
            AuditLog(context),
            NullLogger<ReapplyErasuresCommandHandler>.Instance);
    }

    private static async Task<bool> VoidAuditedAsync(AppDbContext context, Guid invitationId) =>
        await context.AuditLogs.AsNoTracking().AnyAsync(entry =>
            entry.Action == ScopeOwnerInvitationAuditActions.Voided && entry.TargetId == invitationId && entry.Succeeded);

    private static Person DeletedPerson(DateTime deletedAt, DeletionKinds kind) => new()
    {
        PublicId = Guid.NewGuid(),
        Name = "Ada Lovelace",
        Email = $"ada-{Guid.NewGuid():N}@functional.test",
        PasswordHash = [1, 2, 3],
        Salt = [4, 5, 6],
        RoleId = (long)Roles.User,
        EmailVerified = true,
        IsDeleted = true,
        DeletedAt = deletedAt,
        DeletionKind = (int)kind
    };

    [FunctionalFact]
    public async Task GivenMoreFullBatchesThanTheCap_WhenAnonymising_ThenPostgreSqlCountsTheIdentitiesStillDue()
    {
        // Given nothing else due, then seven persons and five Google Users past their window
        await using (var drain = fixture.CreateContext())
        {
            for (var run = 0; run < 20 && (await Handler(drain).HandleAsync(new AnonymiseExpiredDeletionsCommand()))
                     .Data!.TotalAnonymised > 0; run++)
            {
            }
        }

        await using var context = fixture.CreateContext();
        var scope = new Scope { PublicId = Guid.NewGuid(), Name = $"Backlog {Guid.NewGuid():N}" };
        context.Scopes.Add(scope);
        await context.SaveChangesAsync();

        context.Persons.AddRange(Enumerable.Range(0, 7)
            .Select(i => DeletedPerson(DateTime.UtcNow.AddDays(-200 - i), DeletionKinds.Administrative)));
        context.GoogleUsers.AddRange(Enumerable.Range(0, 5).Select(i => new GoogleUser
        {
            PublicId = Guid.NewGuid(), GoogleId = Guid.NewGuid().ToString("N"), Name = "Grace",
            Email = $"backlog-{Guid.NewGuid():N}@functional.test", ScopeId = scope.Id, IsDeleted = true,
            DeletedAt = DateTime.UtcNow.AddDays(-200 - i), DeletionKind = (int)DeletionKinds.Administrative
        }));
        await context.SaveChangesAsync();

        // When — a batch of two, two batches
        await using var runContext = fixture.CreateContext();
        var output = await Handler(runContext, Retention with { PurgeBatchSize = 2, MaxBatchesPerTick = 2 })
            .HandleAsync(new AnonymiseExpiredDeletionsCommand());

        // Then — four of each anonymised, and the database counted what is left of both: three persons and
        // one Google User
        Assert.True(output.Success, string.Join("; ", output.Errors));
        Assert.Equal(4, output.Data!.PersonsAnonymised);
        Assert.Equal(4, output.Data.GoogleUsersAnonymised);
        Assert.Equal(4, output.Data.Backlog);
    }

    [FunctionalFact]
    public async Task GivenRecordsPastTheirWindow_WhenAnonymising_ThenTheDatabaseHoldsNoIdentifyingValue()
    {
        await using var context = fixture.CreateContext();

        // A scope of this test's own, so the two Google Users below collide with each other's
        // placeholders if they collide at all — which is the point
        var scope = new Scope { PublicId = Guid.NewGuid(), Name = $"Retention {Guid.NewGuid():N}" };

        context.Scopes.Add(scope);
        await context.SaveChangesAsync();

        var administrative = DeletedPerson(DateTime.UtcNow.AddDays(-120), DeletionKinds.Administrative);
        var requested = DeletedPerson(DateTime.UtcNow.AddDays(-45), DeletionKinds.SubjectRequested);

        // 45 days: past the statutory deadline, inside the reversal window. It is the record that
        // proves the conditional actually reached SQL — if the deadline were applied uniformly, one
        // of this pair comes out wrong.
        var stillInsideItsWindow = DeletedPerson(DateTime.UtcNow.AddDays(-45), DeletionKinds.Administrative);

        var firstGoogleUser = new GoogleUser
        {
            PublicId = Guid.NewGuid(), GoogleId = Guid.NewGuid().ToString("N"), Name = "Ada",
            Email = $"g1-{Guid.NewGuid():N}@functional.test", ScopeId = scope.Id, IsDeleted = true,
            ProfilePictureUrl = "https://example.invalid/photo.jpg",
            DeletedAt = DateTime.UtcNow.AddDays(-120), DeletionKind = (int)DeletionKinds.Administrative
        };
        var secondGoogleUser = new GoogleUser
        {
            PublicId = Guid.NewGuid(), GoogleId = Guid.NewGuid().ToString("N"), Name = "Grace",
            Email = $"g2-{Guid.NewGuid():N}@functional.test", ScopeId = scope.Id, IsDeleted = true,
            ProfilePictureUrl = "https://example.invalid/photo2.jpg",
            DeletedAt = DateTime.UtcNow.AddDays(-120), DeletionKind = (int)DeletionKinds.Administrative
        };

        context.Persons.AddRange(administrative, requested, stillInsideItsWindow);
        context.GoogleUsers.AddRange(firstGoogleUser, secondGoogleUser);
        await context.SaveChangesAsync();

        context.PasswordResetTokens.Add(new PasswordResetToken
        {
            PersonId = administrative.Id, TokenHash = Guid.NewGuid().ToString("N"),
            ExpiresAt = DateTime.UtcNow.AddDays(1)
        });
        context.TwoFactorAuths.Add(new TwoFactorAuth
        {
            PersonId = administrative.Id, AppEnabled = true, IsActive = true, TotpSecretEncrypted = [7, 7, 7]
        });
        await context.SaveChangesAsync();

        // When
        var output = await Handler(context).HandleAsync(new AnonymiseExpiredDeletionsCommand());

        Assert.True(output.Success);

        await using var verification = fixture.CreateContext();

        // The two due persons carry no identifying value; both unique indexes accepted the writes
        foreach (var due in new[] { administrative, requested })
        {
            var stored = await verification.Persons.SingleAsync(x => x.Id == due.Id);

            Assert.Equal(IdentityAnonymiser.AnonymisedName, stored.Name);
            Assert.EndsWith($"@{IdentityAnonymiser.AnonymisedEmailDomain}", stored.Email);
            Assert.Empty(stored.PasswordHash);
            Assert.Empty(stored.Salt);
            Assert.False(stored.EmailVerified);
            Assert.NotNull(stored.AnonymisedAt);

            // NFR-07: the row is still there and still resolvable
            Assert.Equal(due.PublicId, stored.PublicId);
            Assert.True(stored.IsDeleted);
        }

        // The administrative deletion inside its window is untouched — the conditional reached SQL
        var untouched = await verification.Persons.SingleAsync(x => x.Id == stillInsideItsWindow.Id);

        Assert.Equal("Ada Lovelace", untouched.Name);
        Assert.Null(untouched.AnonymisedAt);

        // Both Google Users anonymised in one scope, so the placeholders did not collide
        var storedGoogleUsers = await verification.GoogleUsers
            .Where(x => x.ScopeId == scope.Id)
            .OrderBy(x => x.Id)
            .ToListAsync();

        Assert.Equal(2, storedGoogleUsers.Count);
        Assert.All(storedGoogleUsers, googleUser =>
        {
            Assert.Equal(IdentityAnonymiser.AnonymisedName, googleUser.Name);
            Assert.Null(googleUser.ProfilePictureUrl);
            Assert.NotNull(googleUser.AnonymisedAt);
        });
        Assert.NotEqual(storedGoogleUsers[0].Email, storedGoogleUsers[1].Email);
        Assert.NotEqual(storedGoogleUsers[0].GoogleId, storedGoogleUsers[1].GoogleId);

        // The dependents went with the person rather than being left behind
        Assert.False(await verification.PasswordResetTokens.AnyAsync(x => x.PersonId == administrative.Id));
        Assert.False(await verification.TwoFactorAuths.AnyAsync(x => x.PersonId == administrative.Id));
    }

    [FunctionalFact]
    public async Task GivenAnAnonymisedRecord_WhenAnonymisingAgain_ThenItIsNotTakenTwice()
    {
        await using var context = fixture.CreateContext();

        var person = DeletedPerson(DateTime.UtcNow.AddDays(-200), DeletionKinds.Administrative);

        context.Persons.Add(person);
        await context.SaveChangesAsync();

        await Handler(context).HandleAsync(new AnonymiseExpiredDeletionsCommand());

        await using var second = fixture.CreateContext();
        var firstPass = (await second.Persons.SingleAsync(x => x.Id == person.Id)).AnonymisedAt;

        await Handler(second).HandleAsync(new AnonymiseExpiredDeletionsCommand());

        await using var verification = fixture.CreateContext();
        var afterSecondPass = await verification.Persons.SingleAsync(x => x.Id == person.Id);

        // The stamp is what makes the pass idempotent: a second run must not rewrite the row
        Assert.Equal(firstPass, afterSecondPass.AnonymisedAt);
    }

    [FunctionalFact]
    public async Task GivenADuePersonWithFactorBudgetsSpent_WhenAnonymising_ThenTheDatabaseHoldsNoCounters()
    {
        // Given — written by the targeted writes only, so the anonymising save cannot clear them
        // on its own (NFR-20)
        await using var context = fixture.CreateContext();
        var person = DeletedPerson(DateTime.UtcNow.AddDays(-120), DeletionKinds.Administrative);
        person.FailedSecondFactorAttempts = 4;
        person.SecondFactorLockedOutUntil = DateTime.UtcNow.AddHours(1);
        person.SecondFactorLockouts = 2;
        person.FailedReauthenticationAttempts = 3;
        person.ReauthenticationWindowStartedAt = DateTime.UtcNow.AddMinutes(-5);
        person.ReauthenticationPausedUntil = DateTime.UtcNow.AddMinutes(5);
        context.Persons.Add(person);
        await context.SaveChangesAsync();

        // When
        var output = await Handler(context).HandleAsync(new AnonymiseExpiredDeletionsCommand());

        // Then
        Assert.True(output.Success);

        await using var verification = fixture.CreateContext();
        var stored = await verification.Persons.SingleAsync(x => x.Id == person.Id);

        Assert.NotNull(stored.AnonymisedAt);
        Assert.Equal(0, stored.FailedSecondFactorAttempts);
        Assert.Null(stored.SecondFactorLockedOutUntil);
        Assert.Equal(0, stored.SecondFactorLockouts);
        Assert.Equal(0, stored.FailedReauthenticationAttempts);
        Assert.Null(stored.ReauthenticationWindowStartedAt);
        Assert.Null(stored.ReauthenticationPausedUntil);
    }

    [FunctionalFact]
    public async Task GivenARestoredIdentity_WhenErasureIsReapplied_ThenItsPasswordHashAndSaltAreEmptiedInTheDatabase()
    {
        // Given a live person a restore brought back. The password columns are written only by
        // targeted writes once the row exists (FR-PR-08), so the reconciliation must clear them so
        await using var context = fixture.CreateContext();
        var restored = DeletedPerson(DateTime.UtcNow, DeletionKinds.SubjectRequested);
        restored.IsDeleted = false;
        restored.DeletedAt = null;
        context.Persons.Add(restored);
        await context.SaveChangesAsync();

        var handler = ReapplyHandler(context);

        // When
        var output = await handler.HandleAsync(new ReapplyErasuresCommand
        {
            SubjectIds = [restored.PublicId], ActingRole = (int)Roles.SystemAdmin, ActingPersonId = Guid.NewGuid()
        });

        // Then
        Assert.True(output.Success);
        await using var verification = fixture.CreateContext();
        var stored = await verification.Persons.SingleAsync(x => x.Id == restored.Id);
        Assert.NotNull(stored.AnonymisedAt);
        Assert.Empty(stored.PasswordHash);
        Assert.Empty(stored.Salt);
    }

    [FunctionalFact]
    public async Task GivenAnErasedInviteeWithAnOpenInvitation_WhenAnonymising_ThenTheInvitationIsVoided()
    {
        // Given — the spec's "erasure voids the invitation", on PostgreSQL, and the erased person also
        // named as the inviter of another
        await using var context = fixture.CreateContext();
        var scope = new Scope { PublicId = Guid.NewGuid(), Name = $"scope-{Guid.NewGuid():N}" };
        context.Scopes.Add(scope);
        var person = DeletedPerson(DateTime.UtcNow - TimeSpan.FromDays(31), DeletionKinds.SubjectRequested);
        person.RoleId = (long)Roles.ScopeAdmin;
        var other = new Person
        {
            PublicId = Guid.NewGuid(), Name = "Invitee", Email = $"invitee-{Guid.NewGuid():N}@functional.test",
            PasswordHash = [1], Salt = [2], RoleId = (long)Roles.ScopeAdmin
        };
        context.Persons.AddRange(person, other);
        await context.SaveChangesAsync();
        var invitation = new ScopeOwnerInvitation
        {
            PublicId = Guid.NewGuid(), ScopeId = scope.Id, InviteePersonId = person.Id,
            CreatedAt = DateTime.UtcNow.AddDays(-2), ExpiresAt = DateTime.UtcNow.AddDays(5)
        };
        var sent = new ScopeOwnerInvitation
        {
            PublicId = Guid.NewGuid(), ScopeId = scope.Id, InviteePersonId = other.Id, InvitedByPersonId = person.Id,
            CreatedAt = DateTime.UtcNow.AddDays(-2), ExpiresAt = DateTime.UtcNow.AddDays(5)
        };
        context.ScopeOwnerInvitations.AddRange(invitation, sent);
        await context.SaveChangesAsync();

        // When
        var output = await Handler(context).HandleAsync(new AnonymiseExpiredDeletionsCommand());

        // Then
        Assert.True(output.Success);
        await using var check = fixture.CreateContext();
        Assert.True(await VoidAuditedAsync(check, invitation.PublicId));
        var stored = await check.ScopeOwnerInvitations.AsNoTracking().SingleAsync(x => x.Id == invitation.Id);
        Assert.Equal(ScopeOwnerInvitationOutcomes.Voided, stored.Outcome);
        Assert.NotNull(stored.EndedAt);
        var storedSent = await check.ScopeOwnerInvitations.AsNoTracking().SingleAsync(x => x.Id == sent.Id);
        Assert.Null(storedSent.InvitedByPersonId);
        Assert.Null(storedSent.EndedAt);
    }

    [FunctionalFact]
    public async Task GivenAnAnonymisedPersonStillNamedByInvitations_WhenAnonymising_ThenTheyAreForgottenAndAudited()
    {
        // Given — a person an earlier run anonymised and then failed to forget: still invited, and still
        // named as the inviter of another invitation
        await using var context = fixture.CreateContext();
        var scope = new Scope { PublicId = Guid.NewGuid(), Name = $"scope-{Guid.NewGuid():N}" };
        context.Scopes.Add(scope);
        var person = DeletedPerson(DateTime.UtcNow.AddDays(-60), DeletionKinds.SubjectRequested);
        person.RoleId = (long)Roles.ScopeAdmin;
        var other = new Person
        {
            PublicId = Guid.NewGuid(), Name = "Invitee", Email = $"invitee-{Guid.NewGuid():N}@functional.test",
            PasswordHash = [1], Salt = [2], RoleId = (long)Roles.ScopeAdmin
        };
        context.Persons.AddRange(person, other);
        await context.SaveChangesAsync();
        var invitation = new ScopeOwnerInvitation
        {
            PublicId = Guid.NewGuid(), ScopeId = scope.Id, InviteePersonId = person.Id,
            CreatedAt = DateTime.UtcNow.AddDays(-2), ExpiresAt = DateTime.UtcNow.AddDays(5)
        };
        var sent = new ScopeOwnerInvitation
        {
            PublicId = Guid.NewGuid(), ScopeId = scope.Id, InviteePersonId = other.Id, InvitedByPersonId = person.Id,
            CreatedAt = DateTime.UtcNow.AddDays(-2), ExpiresAt = DateTime.UtcNow.AddDays(5)
        };
        context.ScopeOwnerInvitations.AddRange(invitation, sent);
        await context.SaveChangesAsync();
        Assert.True(await IdentityAnonymiser.AnonymiseAsync(new AtomicWrites(context), person, DateTime.UtcNow));

        // When
        var output = await Handler(context).HandleAsync(new AnonymiseExpiredDeletionsCommand());

        // Then
        Assert.True(output.Success);
        await using var check = fixture.CreateContext();
        var stored = await check.ScopeOwnerInvitations.AsNoTracking().SingleAsync(x => x.Id == invitation.Id);
        Assert.Equal(ScopeOwnerInvitationOutcomes.Voided, stored.Outcome);
        Assert.True(await VoidAuditedAsync(check, invitation.PublicId));
        var storedSent = await check.ScopeOwnerInvitations.AsNoTracking().SingleAsync(x => x.Id == sent.Id);
        Assert.Null(storedSent.InvitedByPersonId);
        Assert.Null(storedSent.EndedAt);
    }

    [FunctionalFact]
    public async Task GivenARestoredPersonWithInvitations_WhenErasureIsReapplied_ThenTheyAreForgottenAndAudited()
    {
        // Given — a restore brought back a person, an invitation open to them and one they sent
        await using var context = fixture.CreateContext();
        var scope = new Scope { PublicId = Guid.NewGuid(), Name = $"scope-{Guid.NewGuid():N}" };
        context.Scopes.Add(scope);
        var restored = DeletedPerson(DateTime.UtcNow, DeletionKinds.SubjectRequested);
        restored.IsDeleted = false;
        restored.DeletedAt = null;
        restored.RoleId = (long)Roles.ScopeAdmin;
        var other = new Person
        {
            PublicId = Guid.NewGuid(), Name = "Invitee", Email = $"invitee-{Guid.NewGuid():N}@functional.test",
            PasswordHash = [1], Salt = [2], RoleId = (long)Roles.ScopeAdmin
        };
        context.Persons.AddRange(restored, other);
        await context.SaveChangesAsync();
        var invitation = new ScopeOwnerInvitation
        {
            PublicId = Guid.NewGuid(), ScopeId = scope.Id, InviteePersonId = restored.Id,
            CreatedAt = DateTime.UtcNow.AddDays(-2), ExpiresAt = DateTime.UtcNow.AddDays(5)
        };
        var sent = new ScopeOwnerInvitation
        {
            PublicId = Guid.NewGuid(), ScopeId = scope.Id, InviteePersonId = other.Id,
            InvitedByPersonId = restored.Id, CreatedAt = DateTime.UtcNow.AddDays(-2), ExpiresAt = DateTime.UtcNow.AddDays(5)
        };
        context.ScopeOwnerInvitations.AddRange(invitation, sent);
        await context.SaveChangesAsync();

        // When
        var output = await ReapplyHandler(context).HandleAsync(new ReapplyErasuresCommand
        {
            SubjectIds = [restored.PublicId], ActingRole = (int)Roles.SystemAdmin, ActingPersonId = Guid.NewGuid()
        });

        // Then
        Assert.True(output.Success);
        Assert.Equal(1, output.Data!.ScopeOwnerInvitationsVoided);
        await using var check = fixture.CreateContext();
        var stored = await check.ScopeOwnerInvitations.AsNoTracking().SingleAsync(x => x.Id == invitation.Id);
        Assert.Equal(ScopeOwnerInvitationOutcomes.Voided, stored.Outcome);
        Assert.NotNull(stored.EndedAt);
        Assert.True(await VoidAuditedAsync(check, invitation.PublicId));
        var storedSent = await check.ScopeOwnerInvitations.AsNoTracking().SingleAsync(x => x.Id == sent.Id);
        Assert.Null(storedSent.InvitedByPersonId);
        Assert.Null(storedSent.EndedAt);
    }
}
