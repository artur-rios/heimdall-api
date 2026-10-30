using ArturRios.Data.Relational.Core.Repositories;
using ArturRios.Heimdall.Command.Handlers;
using ArturRios.Heimdall.Command.Input;
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
using static ArturRios.Heimdall.WebApi.Tests.Support.ApplicationSeeds;

namespace ArturRios.Heimdall.WebApi.Tests;

/// <summary>
///     Issue #205 for Google Users, against the real database: a writer that read a Google User and writes after
///     another request changed it writes only its own columns, so a restriction (UC-44), an erasure request
///     (UC-42) or a deletion (UC-28) that landed in between survives it.
/// </summary>
/// <remarks>
///     The interleaving is <see cref="InterleavedWriteTests" />'s: each writer runs on its own context, as the
///     runtime login, and a <see cref="BeforeFirstCommand" /> lands the other request's write, committed on a
///     connection of its own, just before the writer's first write to <c>google_user</c> is sent. The competing
///     writes set the columns their handlers set, by one statement each.
/// </remarks>
[Collection(nameof(FunctionalCollection))]
public class GoogleUserInterleavedWriteTests(PostgresFixture db)
{
    private const string GoogleUserWrite = "UPDATE heimdall.google_user";
    private const int FirstGround = (int)RestrictionGrounds.AccuracyContested;
    private const int SecondGround = (int)RestrictionGrounds.ObjectionPending;

    // UC-28: Logical Delete Google User

    [FunctionalFact]
    public async Task GivenARestrictionAfterTheRead_WhenDeletingTheGoogleUser_ThenTheRestrictionSurvivesTheDeletion()
    {
        // Given
        var (scope, googleUser) = await SeedGoogleUserAsync();
        await using var writer = Writer(Restriction(googleUser.Id, FirstGround));

        // When
        var output = await DeleteGoogleUser(writer).HandleAsync(Deletion(scope, googleUser));

        // Then
        Assert.True(output.Success, string.Join(" | ", output.Errors));
        Assert.False(output.Data!.AlreadyDeleted);
        var stored = await StoredAsync(googleUser.Id);
        Assert.True(stored.IsDeleted);
        Assert.Equal((int)DeletionKinds.Administrative, stored.DeletionKind);
        Assert.Equal(FirstGround, stored.RestrictionGround);
        Assert.NotNull(stored.ProcessingRestrictedAt);
    }

    [FunctionalFact]
    public async Task GivenAnErasureRequestAfterTheRead_WhenDeletingTheGoogleUser_ThenTheRequestAndItsSuspensionSurvive()
    {
        // Given
        var (scope, googleUser) = await SeedGoogleUserAsync();
        await using var writer = Writer(ErasureRequest(googleUser.Id));

        // When
        var output = await DeleteGoogleUser(writer).HandleAsync(Deletion(scope, googleUser));

        // Then the subject's suspension already did what was asked (AF-28b), and keeps its own kind
        Assert.True(output.Success, string.Join(" | ", output.Errors));
        Assert.True(output.Data!.AlreadyDeleted);
        AssertErasureRequested(await StoredAsync(googleUser.Id));
    }

    // UC-44: Restrict Processing, for a Google User

    [FunctionalFact]
    public async Task GivenARestrictionAfterTheRead_WhenRestrictingTheGoogleUser_ThenTheFirstRestrictionStands()
    {
        // Given
        var (_, googleUser) = await SeedGoogleUserAsync();
        await using var writer = Writer(Restriction(googleUser.Id, FirstGround));

        // When
        var output = await Restrict(writer).HandleAsync(Restriction(googleUser, SecondGround));

        // Then it is refused as a restriction already in force would have been (AF-44a)
        Assert.Equal([ErasureMessages.ProcessingAlreadyRestricted], output.Errors);
        Assert.Equal(FirstGround, (await StoredAsync(googleUser.Id)).RestrictionGround);
    }

    [FunctionalFact]
    public async Task GivenAnErasureRequestAfterTheRead_WhenRestrictingTheGoogleUser_ThenTheRequestSurvives()
    {
        // Given
        var (_, googleUser) = await SeedGoogleUserAsync();
        await using var writer = Writer(ErasureRequest(googleUser.Id));

        // When
        var output = await Restrict(writer).HandleAsync(Restriction(googleUser, SecondGround));

        // Then the restriction is imposed after the request, which it holds (NFR-24), and the request stands
        Assert.True(output.Success, string.Join(" | ", output.Errors));
        var stored = await StoredAsync(googleUser.Id);
        AssertErasureRequested(stored);
        Assert.Equal(SecondGround, stored.RestrictionGround);
    }

    // UC-45: Lift Processing Restriction, for a Google User

    [FunctionalFact]
    public async Task GivenAnErasureRequestAfterTheRead_WhenLiftingTheGoogleUsersRestriction_ThenTheRequestSurvives()
    {
        // Given a restricted Google User whose erasure request — which lifts the restriction itself — lands
        // between the lift's read and its write
        var (_, googleUser) = await SeedGoogleUserAsync(restricted: true);
        await using var writer = Writer(ErasureRequest(googleUser.Id));

        // When the subject lifts their own
        var output = await Lift(writer).HandleAsync(new LiftProcessingRestrictionCommand
        {
            ActingPersonId = googleUser.PublicId, ActingRole = (int)Roles.User
        });

        // Then there is no restriction left to lift (AF-45a), and the request stands
        Assert.Equal([ErasureMessages.NotRestricted], output.Errors);
        AssertErasureRequested(await StoredAsync(googleUser.Id));
    }

    // UC-25: Sign Up / Sign In via Google, the email-verified refresh (FR-GO-19)

    [FunctionalFact]
    public async Task GivenADeletionAfterTheRead_WhenSigningInWithAFresherVerifiedFlag_ThenItIsRefusedAndTheDeletionSurvives()
    {
        // Given an unverified Google User, a token that now says verified, and UC-28 deleting the account
        // between the sign-in's checks and the refresh
        var (scope, googleUser) = await SeedGoogleUserAsync(emailVerified: false);
        await using var writer = Writer(Deletion(googleUser.Id));
        var payload = new GoogleIdTokenPayload(
            googleUser.GoogleId, googleUser.Email, EmailVerified: true, googleUser.Name, PictureUrl: null,
            TokenId: Guid.NewGuid().ToString("N"), ExpiresAt: DateTime.UtcNow.AddMinutes(5));

        // When
        var output = await SignIn(writer, payload).HandleAsync(new GoogleSignInCommand
        {
            ScopeId = scope.PublicId, IdToken = "stand-in"
        });

        // Then the deleted account is refused as AF-25d refuses it, and stays deleted
        Assert.Equal([AuthMessages.GoogleAuthenticationFailed], output.Errors);
        var stored = await StoredAsync(googleUser.Id);
        Assert.True(stored.IsDeleted);
        Assert.Equal((int)DeletionKinds.Administrative, stored.DeletionKind);
        Assert.False(stored.EmailVerified);
    }

    // NFR-20: the anonymisation pass

    [FunctionalFact]
    public async Task GivenARestrictionAfterTheSelection_WhenAnonymising_ThenTheGoogleUserIsPreservedAsItStands()
    {
        // Given a Google User deleted past its window, restricted between the pass's selection and its write
        var (_, googleUser) = await SeedGoogleUserAsync(deletedDaysAgo: 400);
        await using var writer = Writer(Restriction(googleUser.Id, FirstGround));

        // When
        var output = await IdentityAnonymisationTests.Handler(writer).HandleAsync(new AnonymiseExpiredDeletionsCommand());

        // Then a restricted record is not anonymised (NFR-24), and its restriction stands
        Assert.True(output.Success, string.Join(" | ", output.Errors));
        Assert.DoesNotContain(googleUser.PublicId, output.Data!.AnonymisedIds);
        var stored = await StoredAsync(googleUser.Id);
        Assert.Null(stored.AnonymisedAt);
        Assert.Equal(googleUser.Name, stored.Name);
        Assert.Equal(FirstGround, stored.RestrictionGround);
    }

    // The writer, and the writes that land under it

    private AppDbContext Writer(Func<Task> competitor) =>
        PostgresFixture.CreateContext(db.RuntimeConnectionString, (IInterceptor)new BeforeFirstCommand(GoogleUserWrite, competitor));

    /// <summary>UC-44's columns, on a connection of their own.</summary>
    private Func<Task> Restriction(long googleUserId, int ground) => () => OnGoogleUserAsync(googleUserId, rows =>
        rows.ExecuteUpdateAsync(set => set
            .SetProperty(x => x.ProcessingRestrictedAt, DateTime.UtcNow)
            .SetProperty(x => x.RestrictionGround, ground)));

    /// <summary>UC-42's columns for a Google User — the request, the lifted restriction, the suspension.</summary>
    private Func<Task> ErasureRequest(long googleUserId) => () => OnGoogleUserAsync(googleUserId, rows =>
    {
        var now = DateTime.UtcNow;

        return rows.ExecuteUpdateAsync(set => set
            .SetProperty(x => x.ErasureRequestedAt, now)
            .SetProperty(x => x.ErasureDueAt, now.AddDays(30))
            .SetProperty(x => x.ProcessingRestrictedAt, (DateTime?)null)
            .SetProperty(x => x.RestrictionGround, (int?)null)
            .SetProperty(x => x.IsDeleted, true)
            .SetProperty(x => x.DeletedAt, now)
            .SetProperty(x => x.DeletionKind, (int)DeletionKinds.SubjectRequested));
    });

    /// <summary>UC-28's columns, on a connection of their own.</summary>
    private Func<Task> Deletion(long googleUserId) => () => OnGoogleUserAsync(googleUserId, rows =>
        rows.ExecuteUpdateAsync(set => set
            .SetProperty(x => x.IsDeleted, true)
            .SetProperty(x => x.DeletedAt, DateTime.UtcNow)
            .SetProperty(x => x.DeletionKind, (int)DeletionKinds.Administrative)));

    private async Task OnGoogleUserAsync(long googleUserId, Func<IQueryable<GoogleUser>, Task<int>> write)
    {
        await using var context = db.CreateContext();
        Assert.Equal(1, await write(context.GoogleUsers.Where(x => x.Id == googleUserId)));
    }

    private static void AssertErasureRequested(GoogleUser stored)
    {
        Assert.NotNull(stored.ErasureRequestedAt);
        Assert.NotNull(stored.ErasureDueAt);
        Assert.True(stored.IsDeleted);
        Assert.Equal((int)DeletionKinds.SubjectRequested, stored.DeletionKind);
    }

    // Seeds and reads

    private async Task<(Scope Scope, GoogleUser GoogleUser)> SeedGoogleUserAsync(
        bool emailVerified = true, bool restricted = false, int? deletedDaysAgo = null)
    {
        var scope = await Seeds.ScopeAsync(db);

        await using var context = db.CreateContext();
        await context.Scopes.Where(x => x.Id == scope.Id)
            .ExecuteUpdateAsync(set => set.SetProperty(x => x.GoogleSignInEnabled, true));

        var googleUser = new GoogleUser
        {
            PublicId = Guid.NewGuid(), GoogleId = $"google-{Guid.NewGuid():N}", Name = "Interleaved Google User",
            Email = $"interleaved-{Guid.NewGuid():N}@gmail.test", ScopeId = scope.Id, EmailVerified = emailVerified,
            ProcessingRestrictedAt = restricted ? DateTime.UtcNow.AddDays(-1) : null,
            RestrictionGround = restricted ? FirstGround : null,
            IsDeleted = deletedDaysAgo is not null,
            DeletedAt = deletedDaysAgo is { } days ? DateTime.UtcNow.AddDays(-days) : null,
            DeletionKind = deletedDaysAgo is not null ? (int)DeletionKinds.Administrative : null
        };
        context.GoogleUsers.Add(googleUser);
        await context.SaveChangesAsync();

        return (scope, googleUser);
    }

    private async Task<GoogleUser> StoredAsync(long googleUserId)
    {
        await using var context = db.CreateContext();

        return await context.GoogleUsers.AsNoTracking().SingleAsync(x => x.Id == googleUserId);
    }

    // Commands

    private static DeleteGoogleUserCommand Deletion(Scope scope, GoogleUser googleUser) => new()
    {
        ScopeId = scope.PublicId, Id = googleUser.PublicId,
        ActingRole = (int)Roles.SystemAdmin, ActingPersonId = Guid.NewGuid()
    };

    private static RestrictProcessingCommand Restriction(GoogleUser googleUser, int ground) => new()
    {
        Ground = ground, ActingPersonId = googleUser.PublicId, ActingRole = (int)Roles.User
    };

    // Handlers, over EF repositories on the writer's context

    private static DeleteGoogleUserCommandHandler DeleteGoogleUser(AppDbContext context)
    {
        var googleUsers = new EfRepository<GoogleUser, long>(context);

        return new DeleteGoogleUserCommandHandler(googleUsers, new AtomicWrites(context),
            new ScopeOwnershipChecker(new EfRepository<Person, long>(context)));
    }

    private static RestrictProcessingCommandHandler Restrict(AppDbContext context)
    {
        return new RestrictProcessingCommandHandler(new EfRepository<Person, long>(context),
            new EfRepository<GoogleUser, long>(context), new AtomicWrites(context));
    }

    private static LiftProcessingRestrictionCommandHandler Lift(AppDbContext context)
    {
        return new LiftProcessingRestrictionCommandHandler(new EfRepository<Person, long>(context),
            new EfRepository<GoogleUser, long>(context), new NoLiftNotices(), new AtomicWrites(context));
    }

    private static GoogleSignInCommandHandler SignIn(AppDbContext context, GoogleIdTokenPayload payload)
    {
        var googleUsers = new EfRepository<GoogleUser, long>(context);

        return new GoogleSignInCommandHandler(new FixedVerifier(payload), new EfRepository<Scope, long>(context),
            new EfRepository<Person, long>(context), googleUsers, googleUsers, new StandInTokens(),
            new AtomicWrites(context));
    }

    /// <summary>A verifier that vouches for one payload, whatever token it is shown.</summary>
    private sealed class FixedVerifier(GoogleIdTokenPayload payload) : IGoogleIdTokenVerifier
    {
        public Task<GoogleIdTokenPayload?> VerifyAsync(string idToken) => Task.FromResult<GoogleIdTokenPayload?>(payload);
    }

    private sealed class StandInTokens : IAuthTokenIssuer
    {
        public Task<AuthToken> IssueAsync(AuthTokenSubject subject) =>
            Task.FromResult(new AuthToken("stand-in", DateTime.UtcNow.AddMinutes(5)));

        public Task<AuthToken> IssueSubjectRightsOnlyAsync(Guid subjectId, int roleId, Guid? securityStamp) =>
            Task.FromResult(new AuthToken("stand-in-rights-only", DateTime.UtcNow.AddMinutes(5)));
    }

    private sealed class NoLiftNotices : IRestrictionLiftNotifier
    {
        public Task<bool> NotifyAsync(string email) => Task.FromResult(true);
    }
}
