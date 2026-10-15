using ArturRios.Heimdall.Command.Handlers;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Tests.Support;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Mock;

namespace ArturRios.Heimdall.Command.Tests;

// Unit tests for HardDeleteGoogleUserCommandHandler (UC-29, FR-GO-16): the main flow, the logically
// deleted record a cleanup pass starts from, and every shape of AF-29a — unknown id, a Google User
// belonging to another scope, and a repeated call. Also the tombstone (#195): written for exactly the
// Google User removed, before it is removed, never on a refusal, and harmlessly again on a retry.
//
// There is no authorization test here: UC-29's only actor is the System Admin and the endpoint's
// RoleRequirement settles it, so the command carries no acting person and the handler applies no
// rule. GoogleUserControllerHardDeleteTests proves the endpoint enforces it.
public class HardDeleteGoogleUserCommandHandlerTests
{
    private static readonly Guid ScopePublicId = Guid.NewGuid();

    private static async Task<GoogleUser> SeedGoogleUserAsync(
        AsyncFakeRepository<GoogleUser, long> googleUsers,
        bool isDeleted = false,
        Guid? scopePublicId = null)
    {
        var scope = new Scope
        {
            Id = 42,
            PublicId = scopePublicId ?? ScopePublicId,
            Name = $"scope-{Guid.NewGuid():N}"
        };

        var googleUser = new Bogus.Faker<GoogleUser>()
            .RuleFor(x => x.PublicId, _ => Guid.NewGuid())
            .RuleFor(x => x.GoogleId, _ => $"google-sub-{Guid.NewGuid():N}")
            .RuleFor(x => x.IsDeleted, _ => isDeleted)
            .RuleFor(x => x.ScopeId, _ => scope.Id)
            .RuleFor(x => x.Scope, _ => scope)
            .Generate();

        await googleUsers.CreateAsync(googleUser);

        return googleUser;
    }

    private static HardDeleteGoogleUserCommand Command(Guid id) =>
        new() { ScopeId = ScopePublicId, Id = id };

    [UnitFact]
    public async Task GivenGoogleUser_WhenHandlingHardDelete_ThenRemovesTheRecord()
    {
        // Given an active Google User (UC-29 main flow)
        var googleUsers = new AsyncFakeRepository<GoogleUser, long>();
        var googleUser = await SeedGoogleUserAsync(googleUsers);
        var handler = new HardDeleteGoogleUserCommandHandler(googleUsers, googleUsers, new InMemoryErasedSubjects());

        // When
        var output = await handler.HandleAsync(Command(googleUser.PublicId));

        // Then — output
        Assert.True(output.Success);
        Assert.Contains(GoogleUserMessages.GoogleUserHardDeletedSuccessfully, output.Messages);
        Assert.Equal(googleUser.PublicId, output.Data!.Id);

        // Then — the row is gone for good (FR-GO-16), not merely flagged
        Assert.Empty((await googleUsers.GetAllAsync()).Data!);
    }

    [UnitFact]
    public async Task GivenLogicallyDeletedGoogleUser_WhenHandlingHardDelete_ThenRemovesTheRecord()
    {
        // Given a Google User UC-28 already soft-deleted — exactly what a cleanup pass starts from,
        // which is why the lookup omits an !IsDeleted filter
        var googleUsers = new AsyncFakeRepository<GoogleUser, long>();
        var googleUser = await SeedGoogleUserAsync(googleUsers, isDeleted: true);
        var handler = new HardDeleteGoogleUserCommandHandler(googleUsers, googleUsers, new InMemoryErasedSubjects());

        // When
        var output = await handler.HandleAsync(Command(googleUser.PublicId));

        // Then
        Assert.True(output.Success);
        Assert.Empty((await googleUsers.GetAllAsync()).Data!);
    }

    [UnitFact]
    public async Task GivenNoSuchGoogleUser_WhenHandlingHardDelete_ThenReturnsNotFoundError()
    {
        // Given an id nobody holds (AF-29a)
        var googleUsers = new AsyncFakeRepository<GoogleUser, long>();
        await SeedGoogleUserAsync(googleUsers);
        var erasedSubjects = new InMemoryErasedSubjects();
        var handler = new HardDeleteGoogleUserCommandHandler(googleUsers, googleUsers, erasedSubjects);

        // When
        var output = await handler.HandleAsync(Command(Guid.NewGuid()));

        // Then — and the Google User that does exist was left alone, with no tombstone
        Assert.False(output.Success);
        Assert.Contains(GoogleUserMessages.GoogleUserNotFound, output.Errors);
        Assert.Single((await googleUsers.GetAllAsync()).Data!);
        Assert.Empty(erasedSubjects.Recorded);
    }

    [UnitFact]
    public async Task GivenGoogleUserInAnotherScope_WhenHandlingHardDelete_ThenReturnsNotFoundError()
    {
        // Given a Google User that exists but belongs to another scope — not the resource this path
        // addresses (AF-29a)
        var googleUsers = new AsyncFakeRepository<GoogleUser, long>();
        var googleUser = await SeedGoogleUserAsync(googleUsers, scopePublicId: Guid.NewGuid());
        var erasedSubjects = new InMemoryErasedSubjects();
        var handler = new HardDeleteGoogleUserCommandHandler(googleUsers, googleUsers, erasedSubjects);

        // When
        var output = await handler.HandleAsync(Command(googleUser.PublicId));

        // Then — refused, and untouched: a wrong-scope path must not destroy anything, nor tombstone it
        Assert.False(output.Success);
        Assert.Contains(GoogleUserMessages.GoogleUserNotFound, output.Errors);
        Assert.Single((await googleUsers.GetAllAsync()).Data!);
        Assert.Empty(erasedSubjects.Recorded);
    }

    [UnitFact]
    public async Task GivenAlreadyHardDeletedGoogleUser_WhenHandlingHardDeleteAgain_ThenReturnsNotFoundError()
    {
        // Given a second call for a record the first one removed. UC-29 defines no idempotent path,
        // unlike UC-28's AF-28b, so the repeat is AF-29a.
        var googleUsers = new AsyncFakeRepository<GoogleUser, long>();
        var googleUser = await SeedGoogleUserAsync(googleUsers);
        var handler = new HardDeleteGoogleUserCommandHandler(googleUsers, googleUsers, new InMemoryErasedSubjects());
        await handler.HandleAsync(Command(googleUser.PublicId));

        // When
        var output = await handler.HandleAsync(Command(googleUser.PublicId));

        // Then
        Assert.False(output.Success);
        Assert.Contains(GoogleUserMessages.GoogleUserNotFound, output.Errors);
    }

    [UnitFact]
    public async Task GivenGoogleUser_WhenHandlingHardDelete_ThenTheirIdIsTombstonedBeforeTheRecordGoes()
    {
        // Given a Google User, watched at the moment the tombstone is written
        var googleUsers = new AsyncFakeRepository<GoogleUser, long>();
        var bystander = await SeedGoogleUserAsync(googleUsers);
        var googleUser = await SeedGoogleUserAsync(googleUsers);
        var present = false;
        var erasedSubjects = new InMemoryErasedSubjects(_ =>
            present = googleUsers.Query().Any(g => g.PublicId == googleUser.PublicId));
        var handler = new HardDeleteGoogleUserCommandHandler(googleUsers, googleUsers, erasedSubjects);
        var before = DateTime.UtcNow;

        // When
        var output = await handler.HandleAsync(Command(googleUser.PublicId));

        // Then — one tombstone, for exactly the Google User removed, written while it was still there
        Assert.True(output.Success);
        var tombstone = Assert.Single(erasedSubjects.Recorded);
        Assert.Equal(googleUser.PublicId, tombstone.Id);
        Assert.Equal((int)ErasedSubjectKinds.GoogleUser, tombstone.Kind);
        Assert.InRange(tombstone.CreatedAt, before, DateTime.UtcNow);
        Assert.True(present);
        Assert.Equal(bystander.PublicId, Assert.Single((await googleUsers.GetAllAsync()).Data!).PublicId);
    }

    [UnitFact]
    public async Task GivenATombstoneFromAFailedAttempt_WhenHandlingHardDeleteAgain_ThenItSucceedsWithOneTombstone()
    {
        // Given a Google User still on record whose earlier hard delete wrote its tombstone and failed
        var googleUsers = new AsyncFakeRepository<GoogleUser, long>();
        var googleUser = await SeedGoogleUserAsync(googleUsers);
        var earlier = DateTime.UtcNow.AddDays(-3);
        var erasedSubjects = new InMemoryErasedSubjects();
        erasedSubjects.Seed(googleUser.PublicId, (int)ErasedSubjectKinds.GoogleUser, earlier);
        var handler = new HardDeleteGoogleUserCommandHandler(googleUsers, googleUsers, erasedSubjects);

        // When
        var output = await handler.HandleAsync(Command(googleUser.PublicId));

        // Then — the retry is not an error, still one row for the id, its grace counted from now
        Assert.True(output.Success);
        var tombstone = Assert.Single(erasedSubjects.Recorded);
        Assert.Equal(googleUser.PublicId, tombstone.Id);
        Assert.True(tombstone.CreatedAt > earlier.AddDays(1));
        Assert.Empty((await googleUsers.GetAllAsync()).Data!);
    }
}
