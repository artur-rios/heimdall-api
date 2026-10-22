using ArturRios.Data.Relational.Core.Repositories;
using ArturRios.Heimdall.Command.Auditing;
using ArturRios.Heimdall.Command.Handlers;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Input.Validation;
using ArturRios.Heimdall.Command.Services;
using ArturRios.Heimdall.Data.Configuration;
using ArturRios.Heimdall.Data.Persistence;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Heimdall.Shared.Services;
using ArturRios.Heimdall.WebApi.Tests.Support;
using ArturRios.Util.Test.Attributes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using static ArturRios.Heimdall.WebApi.Tests.Support.ApplicationSeeds;
using Application = ArturRios.Heimdall.Domain.Entities.Application;

namespace ArturRios.Heimdall.WebApi.Tests;

/// <summary>
///     Issue #205, against the real database: a writer that read a person or a scope, and writes after another
///     request changed it, changes only the columns it means to — a restriction (UC-44) or an erasure request
///     (UC-42) that landed in between survives it, and so do the scope settings the separate endpoints own.
/// </summary>
/// <remarks>
///     <para>
///         The interleaving is made, not hoped for. Each writer runs as one request would — its own context,
///         connected as the runtime login — and a <see cref="BeforeFirstCommand" /> on that context lands the
///         other request's write, committed on a connection of its own, just before the writer's own write
///         reaches the database: the writer's read happened before it, its write happens after it. Where the
///         writer takes locks before writing, the other write lands just before the first lock instead, where a
///         real request could land it, rather than queueing on a lock the held writer can never release.
///     </para>
///     <para>
///         The competing restriction and erasure request are the targeted writes UC-44 and UC-42 make
///         (<see cref="AtomicWrites.TryRestrictProcessingAsync" />,
///         <see cref="AtomicWrites.TryRecordErasureRequestAsync" />); the competing scope settings are written
///         the way their own endpoints change them — Google sign-in (UC-24), self-registration (FR-SR-02)
///         and the email links — one column set at a time.
///     </para>
/// </remarks>
[Collection(nameof(FunctionalCollection))]
public class InterleavedWriteTests(PostgresFixture db)
{
    private const string NewPassword = "Str0ng-Interleave-New-Pass!";
    private const string Renamed = "Renamed by the writer";
    private const string RenamedMeanwhile = "Renamed meanwhile";
    private const string PersonWrite = "UPDATE heimdall.person";
    private const string ScopeWrite = "UPDATE heimdall.scope ";
    private const string OwnerInsert = "INSERT INTO heimdall.scope_owner";
    private const string SystemAdminLock = "SELECT pg_advisory_xact_lock(";
    private const string ScopeLock = "SELECT 1 FROM heimdall.scope WHERE id = ANY(";
    private const string VerificationUrl = "https://interleaved.test/verify";
    private const string ResetUrl = "https://interleaved.test/reset";

    // UC-08: Update Person

    [FunctionalFact]
    public async Task GivenARestrictionAfterTheRead_WhenAnAdminRenamesThePerson_ThenTheRestrictionSurvives()
    {
        // Given
        var (_, person) = await SeedUserAsync();
        await using var writer = Writer(PersonWrite, Restriction(person.Id));

        // When
        var output = await UpdatePerson(writer).HandleAsync(Rename(person));

        // Then the name is written and the restriction stands
        Assert.True(output.Success, string.Join(" | ", output.Errors));
        var stored = await StoredPersonAsync(person.Id);
        AssertRestricted(stored);
        Assert.Equal(Renamed, stored.Name);
    }

    [FunctionalFact]
    public async Task GivenAnErasureRequestAfterTheRead_WhenAnAdminRenamesThePerson_ThenTheRenameIsRefusedAndTheRequestSurvives()
    {
        // Given
        var (_, person) = await SeedUserAsync();
        await using var writer = Writer(PersonWrite, ErasureRequest(person.Id));

        // When
        var output = await UpdatePerson(writer).HandleAsync(Rename(person));

        // Then the person is suspended for erasure, which AF-08a answers, and nothing of the rename lands
        Assert.Equal([PersonMessages.PersonNotFound], output.Errors);
        var stored = await StoredPersonAsync(person.Id);
        AssertErasureRequested(stored);
        Assert.Equal(person.Name, stored.Name);
    }

    // UC-13: Reset Password

    [FunctionalFact]
    public async Task GivenARestrictionAfterTheRead_WhenResettingThePassword_ThenTheResetIsRefusedAndTheRestrictionSurvives()
    {
        // Given
        var (_, person) = await SeedUserAsync();
        var token = await SeedResetTokenAsync(person);
        await using var writer = Writer(PersonWrite, Restriction(person.Id));

        // When
        var output = await ResetPassword(writer)
            .HandleAsync(new ResetPasswordCommand { Token = token, NewPassword = NewPassword });

        // Then
        Assert.Equal([AuthMessages.TokenInvalid], output.Errors);
        var stored = await StoredPersonAsync(person.Id);
        AssertRestricted(stored);
        Assert.Equal(person.PasswordHash, stored.PasswordHash);
    }

    [FunctionalFact]
    public async Task GivenAnErasureRequestAfterTheRead_WhenResettingThePassword_ThenTheResetIsRefusedAndTheRequestSurvives()
    {
        // Given
        var (_, person) = await SeedUserAsync();
        var token = await SeedResetTokenAsync(person);
        await using var writer = Writer(PersonWrite, ErasureRequest(person.Id));

        // When
        var output = await ResetPassword(writer)
            .HandleAsync(new ResetPasswordCommand { Token = token, NewPassword = NewPassword });

        // Then
        Assert.Equal([AuthMessages.TokenInvalid], output.Errors);
        var stored = await StoredPersonAsync(person.Id);
        AssertErasureRequested(stored);
        Assert.Equal(person.PasswordHash, stored.PasswordHash);
    }

    // UC-14: Verify Email

    [FunctionalFact]
    public async Task GivenARestrictionAfterTheRead_WhenVerifyingTheEmail_ThenTheVerificationIsRefusedAndTheRestrictionSurvives()
    {
        // Given
        var (_, person) = await SeedUserAsync(emailVerified: false);
        var token = await SeedVerificationTokenAsync(person);
        await using var writer = Writer(PersonWrite, Restriction(person.Id));

        // When
        var output = await VerifyEmail(writer).HandleAsync(new VerifyEmailCommand { Token = token });

        // Then
        Assert.Equal([AuthMessages.TokenInvalid], output.Errors);
        var stored = await StoredPersonAsync(person.Id);
        AssertRestricted(stored);
        Assert.False(stored.EmailVerified);
    }

    [FunctionalFact]
    public async Task GivenAnErasureRequestAfterTheRead_WhenVerifyingTheEmail_ThenTheVerificationIsRefusedAndTheRequestSurvives()
    {
        // Given
        var (_, person) = await SeedUserAsync(emailVerified: false);
        var token = await SeedVerificationTokenAsync(person);
        await using var writer = Writer(PersonWrite, ErasureRequest(person.Id));

        // When
        var output = await VerifyEmail(writer).HandleAsync(new VerifyEmailCommand { Token = token });

        // Then
        Assert.Equal([AuthMessages.TokenInvalid], output.Errors);
        var stored = await StoredPersonAsync(person.Id);
        AssertErasureRequested(stored);
        Assert.False(stored.EmailVerified);
    }

    // UC-09: Delete Person

    [FunctionalFact]
    public async Task GivenARestrictionAfterTheRead_WhenDeletingThePerson_ThenTheRestrictionSurvivesTheDeletion()
    {
        // Given
        var (_, person) = await SeedUserAsync();
        await using var writer = Writer(PersonWrite, Restriction(person.Id));

        // When
        var output = await DeletePerson(writer).HandleAsync(Deletion(person));

        // Then the person is deleted, and still restricted (NFR-24: restriction and deletion are independent)
        Assert.True(output.Success, string.Join(" | ", output.Errors));
        Assert.False(output.Data!.AlreadyDeleted);
        var stored = await StoredPersonAsync(person.Id);
        AssertRestricted(stored);
        Assert.True(stored.IsDeleted);
        Assert.Equal((int)DeletionKinds.Administrative, stored.DeletionKind);
    }

    [FunctionalFact]
    public async Task GivenAnErasureRequestAfterTheRead_WhenDeletingThePerson_ThenTheRequestAndItsSuspensionSurvive()
    {
        // Given an erasure request that lands between the deletion's read and its locks
        var (_, person) = await SeedUserAsync();
        await using var writer = Writer(SystemAdminLock, ErasureRequest(person.Id));

        // When
        var output = await DeletePerson(writer).HandleAsync(Deletion(person));

        // Then the subject's suspension already did what was asked (AF-09b), and keeps its own kind
        Assert.True(output.Success, string.Join(" | ", output.Errors));
        Assert.True(output.Data!.AlreadyDeleted);
        AssertErasureRequested(await StoredPersonAsync(person.Id));
    }

    // UC-21: Add Scope Owner

    [FunctionalFact]
    public async Task GivenARestrictionAfterTheRead_WhenAddingThePersonAsOwner_ThenTheRestrictionSurvives()
    {
        // Given
        var scope = await SeedScopeAsync(db);
        var person = await SeedScopeAdminAsync(db);
        await using var writer = Writer(OwnerInsert, Restriction(person.Id));

        // When
        var output = await AddScopeOwner(writer).HandleAsync(Ownership(scope, person));

        // Then
        Assert.True(output.Success, string.Join(" | ", output.Errors));
        var stored = await StoredPersonAsync(person.Id);
        AssertRestricted(stored);
        Assert.Equal(scope.Id, Assert.Single(stored.ScopeOwnerships).ScopeId);
    }

    [FunctionalFact]
    public async Task GivenAnErasureRequestAfterTheRead_WhenAddingThePersonAsOwner_ThenTheAddIsRefusedAndTheRequestSurvives()
    {
        // Given
        var scope = await SeedScopeAsync(db);
        var person = await SeedScopeAdminAsync(db);
        await using var writer = Writer(OwnerInsert, ErasureRequest(person.Id));

        // When
        var output = await AddScopeOwner(writer).HandleAsync(Ownership(scope, person));

        // Then the suspended person is no live administrator (AF-21b), and owns nothing
        Assert.Equal([PersonMessages.PersonNotValidScopeAdmin], output.Errors);
        var stored = await StoredPersonAsync(person.Id);
        AssertErasureRequested(stored);
        Assert.Empty(stored.ScopeOwnerships);
    }

    // UC-23: Promote User to Scope Owner

    [FunctionalFact]
    public async Task GivenARestrictionAfterTheRead_WhenPromotingThePerson_ThenTheRestrictionSurvives()
    {
        // Given
        var (scope, person) = await SeedUserAsync();
        await using var writer = Writer(PersonWrite, Restriction(person.Id));

        // When
        var output = await PromoteScopeUser(writer).HandleAsync(Promotion(scope, person));

        // Then the promotion moved both join rows, and the restriction stands
        Assert.True(output.Success, string.Join(" | ", output.Errors));
        var stored = await StoredPersonAsync(person.Id);
        AssertRestricted(stored);
        Assert.Equal((long)Roles.ScopeAdmin, stored.RoleId);
        Assert.Null(stored.ScopeId);
        Assert.Null(stored.ScopeMembership);
        Assert.Equal(scope.Id, Assert.Single(stored.ScopeOwnerships).ScopeId);
    }

    [FunctionalFact]
    public async Task GivenAnErasureRequestAfterTheRead_WhenPromotingThePerson_ThenThePromotionIsRefusedAndTheRequestSurvives()
    {
        // Given
        var (scope, person) = await SeedUserAsync();
        await using var writer = Writer(PersonWrite, ErasureRequest(person.Id));

        // When
        var output = await PromoteScopeUser(writer).HandleAsync(Promotion(scope, person));

        // Then
        Assert.Equal([RelationalErrors.ConcurrencyMessage], output.Errors);
        var stored = await StoredPersonAsync(person.Id);
        AssertErasureRequested(stored);
        Assert.Equal((long)Roles.User, stored.RoleId);
        Assert.NotNull(stored.ScopeMembership);
        Assert.Empty(stored.ScopeOwnerships);
    }

    [FunctionalFact]
    public async Task GivenARenameAfterTheRead_WhenPromotingThePerson_ThenTheNewNameSurvives()
    {
        // Given
        var (scope, person) = await SeedUserAsync();
        await using var writer = Writer(PersonWrite, Rename(person.Id));

        // When
        var output = await PromoteScopeUser(writer).HandleAsync(Promotion(scope, person));

        // Then the promotion writes the role and the join rows, and nothing of the profile it read
        Assert.True(output.Success, string.Join(" | ", output.Errors));
        var stored = await StoredPersonAsync(person.Id);
        Assert.Equal((long)Roles.ScopeAdmin, stored.RoleId);
        Assert.Equal(RenamedMeanwhile, stored.Name);
    }

    // UC-03: Update Scope

    [FunctionalFact]
    public async Task GivenTheSettingsChangedAfterTheRead_WhenUpdatingTheScope_ThenTheSettingsSurvive()
    {
        // Given Google sign-in, self-registration and the email links set by their own endpoints meanwhile
        var scope = await SeedScopeAsync(db);
        await using var writer = Writer(ScopeWrite, Settings(scope.Id));

        // When
        var output = await UpdateScope(writer).HandleAsync(ScopeUpdate(scope));

        // Then the details are written, and none of the settings goes back to what was read
        Assert.True(output.Success, string.Join(" | ", output.Errors));
        var stored = await StoredScopeAsync(scope.Id);
        Assert.Equal($"{scope.Name}-renamed", stored.Name);
        Assert.Equal("Described", stored.Description);
        AssertSettingsSet(stored);
    }

    [FunctionalFact]
    public async Task GivenADeletionAfterTheRead_WhenUpdatingTheScope_ThenTheUpdateIsRefusedAndTheScopeStaysDeleted()
    {
        // Given
        var scope = await SeedScopeAsync(db);
        await using var writer = Writer(ScopeWrite, ScopeDeletion(scope.Id));

        // When
        var output = await UpdateScope(writer).HandleAsync(ScopeUpdate(scope));

        // Then AF-03a answers, and the deleted scope is neither revived nor renamed
        Assert.Equal([ScopeMessages.ScopeNotFound], output.Errors);
        var stored = await StoredScopeAsync(scope.Id);
        Assert.True(stored.IsDeleted);
        Assert.Equal(scope.Name, stored.Name);
    }

    [FunctionalFact]
    public async Task GivenTheNameTakenAfterTheCheck_WhenUpdatingTheScope_ThenAConflictAndNothingIsWritten()
    {
        // Given another scope renamed to the requested name between this request's check and its write
        var scope = await SeedScopeAsync(db);
        var other = await SeedScopeAsync(db);
        var update = ScopeUpdate(scope);
        await using var writer = Writer(ScopeWrite, ScopeRename(other.Id, update.Name));

        // When
        var output = await UpdateScope(writer).HandleAsync(update);

        // Then the unique index refuses it, answered as the repository's save answered it (AF-03b)
        Assert.Equal([RelationalErrors.UniqueViolationMessage], output.Errors);
        var stored = await StoredScopeAsync(scope.Id);
        Assert.Equal(scope.Name, stored.Name);
        Assert.Null(stored.Description);
    }

    // UC-04: Delete Scope

    [FunctionalFact]
    public async Task GivenTheSettingsChangedAfterTheRead_WhenDeletingTheScope_ThenTheSettingsSurvive()
    {
        // Given settings changed between the deletion's read and its lock
        var scope = await SeedScopeAsync(db);
        await using var writer = Writer(ScopeLock, Settings(scope.Id));

        // When
        var output = await DeleteScope(writer).HandleAsync(new DeleteScopeCommand { Id = scope.PublicId });

        // Then the scope is deleted, and writes nothing else of what it read
        Assert.True(output.Success, string.Join(" | ", output.Errors));
        var stored = await StoredScopeAsync(scope.Id);
        Assert.True(stored.IsDeleted);
        AssertSettingsSet(stored);
    }

    // The writer, and the writes that land under it

    /// <summary>
    ///     A context connected as the runtime login, on which <paramref name="competitor" /> runs once, just
    ///     before the first command starting with <paramref name="prefix" /> is sent.
    /// </summary>
    private AppDbContext Writer(string prefix, Func<Task> competitor) =>
        PostgresFixture.CreateContext(db.RuntimeConnectionString, (IInterceptor)new BeforeFirstCommand(prefix, competitor));

    /// <summary>UC-44's write, on a connection of its own.</summary>
    private Func<Task> Restriction(long personId) => () => OnPersonAsync(personId, async (writes, person) =>
        Assert.True(await writes.TryRestrictProcessingAsync(
            person, (int)RestrictionGrounds.AccuracyContested, DateTime.UtcNow)));

    /// <summary>UC-42's write, unblocked, so it suspends the person, on a connection of its own.</summary>
    private Func<Task> ErasureRequest(long personId) => () => OnPersonAsync(personId, async (writes, person) =>
    {
        var now = DateTime.UtcNow;
        Assert.True(await writes.TryRecordErasureRequestAsync(person, now, now.AddDays(30), null));
    });

    /// <summary>Another administrator's rename, on a connection of its own.</summary>
    private Func<Task> Rename(long personId) => async () =>
    {
        await using var context = db.CreateContext();
        await context.Persons.Where(x => x.Id == personId)
            .ExecuteUpdateAsync(set => set.SetProperty(x => x.Name, RenamedMeanwhile));
    };

    /// <summary>The settings their own endpoints own, set on a connection of their own.</summary>
    private Func<Task> Settings(long scopeId) => async () =>
    {
        await using var context = db.CreateContext();
        await context.Scopes.Where(x => x.Id == scopeId).ExecuteUpdateAsync(set => set
            .SetProperty(x => x.GoogleSignInEnabled, true)
            .SetProperty(x => x.SelfRegistrationEnabled, true)
            .SetProperty(x => x.EmailVerificationUrl, VerificationUrl)
            .SetProperty(x => x.PasswordResetUrl, ResetUrl));
    };

    /// <summary>Another scope's update to <paramref name="name" />, on a connection of its own.</summary>
    private Func<Task> ScopeRename(long scopeId, string name) => async () =>
    {
        await using var context = db.CreateContext();
        await context.Scopes.Where(x => x.Id == scopeId)
            .ExecuteUpdateAsync(set => set.SetProperty(x => x.Name, name));
    };

    /// <summary>UC-04's flag, on a connection of its own.</summary>
    private Func<Task> ScopeDeletion(long scopeId) => async () =>
    {
        await using var context = db.CreateContext();
        await context.Scopes.Where(x => x.Id == scopeId)
            .ExecuteUpdateAsync(set => set.SetProperty(x => x.IsDeleted, true));
    };

    private async Task OnPersonAsync(long personId, Func<AtomicWrites, Person, Task> write)
    {
        await using var context = db.CreateContext();
        var person = await context.Persons.SingleAsync(x => x.Id == personId);
        await write(new AtomicWrites(context), person);
    }

    private static void AssertRestricted(Person stored)
    {
        Assert.NotNull(stored.ProcessingRestrictedAt);
        Assert.Equal((int)RestrictionGrounds.AccuracyContested, stored.RestrictionGround);
    }

    private static void AssertErasureRequested(Person stored)
    {
        Assert.NotNull(stored.ErasureRequestedAt);
        Assert.NotNull(stored.ErasureDueAt);
        Assert.True(stored.IsDeleted);
        Assert.Equal((int)DeletionKinds.SubjectRequested, stored.DeletionKind);
    }

    private static void AssertSettingsSet(Scope stored)
    {
        Assert.True(stored.GoogleSignInEnabled);
        Assert.True(stored.SelfRegistrationEnabled);
        Assert.Equal(VerificationUrl, stored.EmailVerificationUrl);
        Assert.Equal(ResetUrl, stored.PasswordResetUrl);
    }

    // Seeds and reads

    /// <summary>A live, verified-or-not User of a fresh scope, the membership row and its copy both set.</summary>
    private async Task<(Scope Scope, Person Person)> SeedUserAsync(bool emailVerified = true)
    {
        var scope = await SeedScopeAsync(db);

        await using var context = db.CreateContext();
        var person = new Person
        {
            PublicId = Guid.NewGuid(), Name = "Interleaved User", Email = $"interleaved-{Guid.NewGuid():N}@test.local",
            PasswordHash = [1, 2, 3], Salt = [4, 5, 6], RoleId = (long)Roles.User,
            EmailVerified = emailVerified, ScopeId = scope.Id
        };
        context.Persons.Add(person);
        await context.SaveChangesAsync();

        context.ScopeUsers.Add(new ScopeUser { ScopeId = scope.Id, PersonId = person.Id });
        await context.SaveChangesAsync();

        return (scope, person);
    }

    private async Task<string> SeedResetTokenAsync(Person person)
    {
        var token = SingleUseToken.New();

        await using var context = db.CreateContext();
        context.PasswordResetTokens.Add(new PasswordResetToken
        {
            PersonId = person.Id, TokenHash = SingleUseTokenHash.Of(token), ExpiresAt = DateTime.UtcNow.AddHours(1)
        });
        await context.SaveChangesAsync();

        return token;
    }

    private async Task<string> SeedVerificationTokenAsync(Person person)
    {
        var token = SingleUseToken.New();

        await using var context = db.CreateContext();
        context.EmailVerificationTokens.Add(new EmailVerificationToken
        {
            PersonId = person.Id, TokenHash = SingleUseTokenHash.Of(token), ExpiresAt = DateTime.UtcNow.AddHours(1)
        });
        await context.SaveChangesAsync();

        return token;
    }

    private async Task<Person> StoredPersonAsync(long personId)
    {
        await using var context = db.CreateContext();

        return await context.Persons.AsNoTracking()
            .Include(x => x.ScopeMembership)
            .Include(x => x.ScopeOwnerships)
            .SingleAsync(x => x.Id == personId);
    }

    private async Task<Scope> StoredScopeAsync(long scopeId)
    {
        await using var context = db.CreateContext();

        return await context.Scopes.AsNoTracking().SingleAsync(x => x.Id == scopeId);
    }

    // Commands

    private static UpdatePersonCommand Rename(Person person) => new()
    {
        Id = person.PublicId, Name = Renamed, Email = person.Email,
        ActingRole = (int)Roles.SystemAdmin, ActingPersonId = Guid.NewGuid()
    };

    private static DeletePersonCommand Deletion(Person person) => new()
    {
        Id = person.PublicId, ActingRole = (int)Roles.SystemAdmin, ActingPersonId = Guid.NewGuid()
    };

    private static AddScopeOwnerCommand Ownership(Scope scope, Person person) => new()
    {
        ScopeId = scope.PublicId, PersonId = person.PublicId,
        ActingRole = (int)Roles.SystemAdmin, ActingPersonId = Guid.NewGuid()
    };

    private static PromoteScopeUserCommand Promotion(Scope scope, Person person) => new()
    {
        ScopeId = scope.PublicId, PersonId = person.PublicId,
        ActingRole = (int)Roles.SystemAdmin, ActingPersonId = Guid.NewGuid()
    };

    private static UpdateScopeCommand ScopeUpdate(Scope scope) => new()
    {
        Id = scope.PublicId, Name = $"{scope.Name}-renamed", Description = "Described"
    };

    // Handlers, over EF repositories on the writer's context

    private static UpdatePersonCommandHandler UpdatePerson(AppDbContext context)
    {
        var persons = new EfRepository<Person, long>(context);

        return new UpdatePersonCommandHandler(new UpdatePersonCommandValidator(), persons,
            new EfRepository<GoogleUser, long>(context), new ScopeOwnershipChecker(persons),
            new EfRepository<EmailVerificationToken, long>(context), new EfRepository<PasswordResetToken, long>(context),
            new AtomicWrites(context), new EfRepository<TwoFactorAuth, long>(context), new NoFactors(),
            new NoEmailChangeNotices(), new AdminEmailOptions(), new NoRetryAfter());
    }

    private static ResetPasswordCommandHandler ResetPassword(AppDbContext context) => new(
        new ResetPasswordCommandValidator(),
        new EfRepository<PasswordResetToken, long>(context),
        new EfRepository<PasswordResetToken, long>(context),
        new AtomicWrites(context),
        new WriteTransactions(context),
        NullLogger<ResetPasswordCommandHandler>.Instance);

    private static VerifyEmailCommandHandler VerifyEmail(AppDbContext context) => new(
        new VerifyEmailCommandValidator(),
        new EfRepository<EmailVerificationToken, long>(context),
        new EfRepository<EmailVerificationToken, long>(context),
        new AtomicWrites(context),
        new PendingRegistrationConfirmer(
            new EfRepository<PendingRegistration, long>(context),
            new EfRepository<PendingRegistration, long>(context),
            new EfRepository<Person, long>(context),
            new EfRepository<GoogleUser, long>(context),
            new EfRepository<Person, long>(context),
            new AtomicWrites(context),
            new WriteTransactions(context),
            NullLogger<PendingRegistrationConfirmer>.Instance),
        new WriteTransactions(context),
        NullLogger<VerifyEmailCommandHandler>.Instance);

    private static DeletePersonCommandHandler DeletePerson(AppDbContext context)
    {
        var persons = new EfRepository<Person, long>(context);

        return new DeletePersonCommandHandler(persons, new ScopeOwnershipChecker(persons), new AtomicWrites(context),
            new WriteTransactions(context), new InvariantLocks(context));
    }

    private static AddScopeOwnerCommandHandler AddScopeOwner(AppDbContext context)
    {
        var persons = new EfRepository<Person, long>(context);

        return new AddScopeOwnerCommandHandler(new EfRepository<Scope, long>(context), persons,
            new ScopeOwnershipChecker(persons), new AtomicWrites(context),
            new EfRepository<ScopeOwnerInvitation, long>(context), new ScopeOwnerInvitations(context),
            new ScopeInvitationOptions(), new AdminEmailOptions(), new NoInvitationNotices(), new NoAudit(),
            NullLogger<AddScopeOwnerCommandHandler>.Instance);
    }

    private static PromoteScopeUserCommandHandler PromoteScopeUser(AppDbContext context)
    {
        var persons = new EfRepository<Person, long>(context);

        return new PromoteScopeUserCommandHandler(new EfRepository<Scope, long>(context), persons,
            new ScopeOwnershipChecker(persons), new AtomicWrites(context));
    }

    private static UpdateScopeCommandHandler UpdateScope(AppDbContext context)
    {
        return new UpdateScopeCommandHandler(new UpdateScopeCommandValidator(), new EfRepository<Scope, long>(context),
            new AtomicWrites(context));
    }

    private static DeleteScopeCommandHandler DeleteScope(AppDbContext context)
    {
        return new DeleteScopeCommandHandler(new EfRepository<Scope, long>(context), new AtomicWrites(context),
            new EfRepository<Person, long>(context),
            new EfRepository<GoogleUser, long>(context), new EfRepository<Application, long>(context),
            new ScopeCascades(context), new WriteTransactions(context), new InvariantLocks(context));
    }

    /// <summary>No second factor: only an own email change asks for one, and these tests make none.</summary>
    private sealed class NoFactors : ITwoFactorFactorVerifier
    {
        public Task<TwoFactorFactorVerificationResult> VerifyAsync(
            TwoFactorAuth twoFactorAuth, string? code, string? recoveryCode) =>
            throw new InvalidOperationException("No second factor is checked by these tests.");
    }

    private sealed class NoEmailChangeNotices : IEmailChangeNoticeSender
    {
        public Task SendAsync(string previousEmail) => Task.CompletedTask;
    }

    private sealed class NoRetryAfter : IRetryAfterAdvisor
    {
        public void Advise(TimeSpan wait)
        {
        }
    }

    private sealed class NoInvitationNotices : IScopeInvitationNoticeSender
    {
        public Task SendInvitationAsync(string inviteeEmail, string inviterName, string scopeName, DateTime expiresAt) =>
            Task.CompletedTask;

        public Task SendAnswerAsync(string inviterEmail, string inviteeName, string scopeName, bool accepted) =>
            Task.CompletedTask;
    }

    private sealed class NoAudit : IAuditLogWriter
    {
        public Task WriteAsync(string action, Guid? targetId, bool succeeded, string? failureReason) =>
            Task.CompletedTask;
    }
}
