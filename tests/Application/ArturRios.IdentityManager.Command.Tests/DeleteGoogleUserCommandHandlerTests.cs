using ArturRios.IdentityManager.Command.Handlers;
using ArturRios.IdentityManager.Command.Input;
using ArturRios.IdentityManager.Domain.Entities;
using ArturRios.IdentityManager.Domain.Enums;
using ArturRios.IdentityManager.Shared.Messages;
using ArturRios.IdentityManager.Shared.Services;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Mock;
using Moq;

namespace ArturRios.IdentityManager.Command.Tests;

// Unit tests for DeleteGoogleUserCommandHandler (UC-28, FR-GO-15): the main flow, AF-28a (unknown
// id, and a Google User addressed through the wrong scope), AF-28b (already deleted, idempotent and
// writing nothing), and AF-28c (a Scope Admin who does not own the scope).
//
// Two of these pin an ordering rather than an outcome: AF-28a's lookup must omit the !IsDeleted
// filter or AF-28b could never fire, and AF-28c must run before AF-28b or an already-deleted record
// becomes a way to probe for Google Users outside the caller's reach.
public class DeleteGoogleUserCommandHandlerTests
{
    private static readonly Guid ScopePublicId = Guid.NewGuid();

    private const long ScopeInternalId = 42;

    private static IScopeOwnershipChecker Ownership(bool mayManage)
    {
        var checker = new Mock<IScopeOwnershipChecker>();
        checker
            .Setup(x => x.ActorMayManageScopeAsync(It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<long>()))
            .ReturnsAsync(mayManage);
        return checker.Object;
    }

    private static async Task<GoogleUser> SeedGoogleUserAsync(
        AsyncFakeRepository<GoogleUser> googleUsers,
        bool isDeleted = false,
        Guid? scopePublicId = null)
    {
        var scope = new Scope
        {
            Id = ScopeInternalId,
            PublicId = scopePublicId ?? ScopePublicId,
            Name = $"scope-{Guid.NewGuid():N}"
        };

        var googleUser = new Bogus.Faker<GoogleUser>()
            .RuleFor(x => x.PublicId, _ => Guid.NewGuid())
            .RuleFor(x => x.GoogleId, _ => $"google-sub-{Guid.NewGuid():N}")
            .RuleFor(x => x.IsDeleted, _ => isDeleted)
            .RuleFor(x => x.UpdatedAt, _ => new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc))
            .RuleFor(x => x.ScopeId, _ => ScopeInternalId)
            .RuleFor(x => x.Scope, _ => scope)
            .Generate();

        await googleUsers.CreateAsync(googleUser);

        return googleUser;
    }

    private static DeleteGoogleUserCommand Command(Guid id, Roles actingRole = Roles.SystemAdmin) =>
        new() { ScopeId = ScopePublicId, Id = id, ActingPersonId = Guid.NewGuid(), ActingRole = (int)actingRole };

    [UnitFact]
    public async Task GivenActiveGoogleUser_WhenHandlingDelete_ThenSetsIsDeletedAndStampsUpdatedAt()
    {
        // Given an authorized caller and an active Google User (UC-28 main flow)
        var googleUsers = new AsyncFakeRepository<GoogleUser>();
        var googleUser = await SeedGoogleUserAsync(googleUsers);
        var before = googleUser.UpdatedAt;
        var handler = new DeleteGoogleUserCommandHandler(googleUsers, googleUsers, Ownership(true));

        // When
        var output = await handler.HandleAsync(Command(googleUser.PublicId));

        // Then — output
        Assert.True(output.Success);
        Assert.Contains(GoogleUserMessages.GoogleUserDeletedSuccessfully, output.Messages);
        Assert.Equal(googleUser.PublicId, output.Data!.Id);
        Assert.False(output.Data.AlreadyDeleted);

        // Then — persisted state (FR-GO-15)
        var stored = (await googleUsers.GetAllAsync()).Data!.Single();
        Assert.True(stored.IsDeleted);
        Assert.True(stored.UpdatedAt > before);
    }

    [UnitFact]
    public async Task GivenOwningScopeAdmin_WhenHandlingDelete_ThenChecksOwnershipOfTheGoogleUsersScope()
    {
        // Given a Scope Admin — UC-28 step 2 grants them the Google Users of the scopes they own
        var googleUsers = new AsyncFakeRepository<GoogleUser>();
        var googleUser = await SeedGoogleUserAsync(googleUsers);
        var checker = new Mock<IScopeOwnershipChecker>();
        checker
            .Setup(x => x.ActorMayManageScopeAsync(It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<long>()))
            .ReturnsAsync(true);
        var handler = new DeleteGoogleUserCommandHandler(googleUsers, googleUsers, checker.Object);
        var command = Command(googleUser.PublicId, Roles.ScopeAdmin);

        // When
        var output = await handler.HandleAsync(command);

        // Then — and the ownership was asked about the Google User's own scope, by internal id
        Assert.True(output.Success);
        checker.Verify(
            x => x.ActorMayManageScopeAsync(
                (int)Roles.ScopeAdmin, command.ActingPersonId, ScopeInternalId),
            Times.Once);
    }

    [UnitFact]
    public async Task GivenNoSuchGoogleUser_WhenHandlingDelete_ThenReturnsNotFoundError()
    {
        // Given an id nobody holds (AF-28a)
        var googleUsers = new AsyncFakeRepository<GoogleUser>();
        await SeedGoogleUserAsync(googleUsers);
        var handler = new DeleteGoogleUserCommandHandler(googleUsers, googleUsers, Ownership(true));

        // When
        var output = await handler.HandleAsync(Command(Guid.NewGuid()));

        // Then
        Assert.False(output.Success);
        Assert.Contains(GoogleUserMessages.GoogleUserNotFound, output.Errors);
    }

    [UnitFact]
    public async Task GivenGoogleUserInAnotherScope_WhenHandlingDelete_ThenReturnsNotFoundError()
    {
        // Given a Google User that exists but belongs to another scope — not the resource this path
        // addresses (AF-28a)
        var googleUsers = new AsyncFakeRepository<GoogleUser>();
        var googleUser = await SeedGoogleUserAsync(googleUsers, scopePublicId: Guid.NewGuid());
        var handler = new DeleteGoogleUserCommandHandler(googleUsers, googleUsers, Ownership(true));

        // When
        var output = await handler.HandleAsync(Command(googleUser.PublicId));

        // Then — and it was not deleted along the way
        Assert.False(output.Success);
        Assert.Contains(GoogleUserMessages.GoogleUserNotFound, output.Errors);
        Assert.False((await googleUsers.GetAllAsync()).Data!.Single().IsDeleted);
    }

    [UnitFact]
    public async Task GivenAlreadyDeletedGoogleUser_WhenHandlingDelete_ThenSucceedsIdempotentlyWithoutWriting()
    {
        // Given a Google User already logically deleted (AF-28b). The lookup has to find it, which is
        // why it omits the !IsDeleted filter UC-27's default read applies.
        var googleUsers = new AsyncFakeRepository<GoogleUser>();
        var googleUser = await SeedGoogleUserAsync(googleUsers, isDeleted: true);
        var before = googleUser.UpdatedAt;
        var handler = new DeleteGoogleUserCommandHandler(googleUsers, googleUsers, Ownership(true));

        // When
        var output = await handler.HandleAsync(Command(googleUser.PublicId));

        // Then — the same 200 and message as the main flow; the flag is what distinguishes them
        Assert.True(output.Success);
        Assert.Contains(GoogleUserMessages.GoogleUserDeletedSuccessfully, output.Messages);
        Assert.True(output.Data!.AlreadyDeleted);

        // Then — UpdatedAt is untouched: re-stamping would misreport when the deletion happened
        var stored = (await googleUsers.GetAllAsync()).Data!.Single();
        Assert.True(stored.IsDeleted);
        Assert.Equal(before, stored.UpdatedAt);
    }

    [UnitFact]
    public async Task GivenNonOwningScopeAdmin_WhenHandlingDelete_ThenReturnsNotAuthorizedError()
    {
        // Given a Scope Admin who does not own the Google User's scope (AF-28c)
        var googleUsers = new AsyncFakeRepository<GoogleUser>();
        var googleUser = await SeedGoogleUserAsync(googleUsers);
        var handler = new DeleteGoogleUserCommandHandler(googleUsers, googleUsers, Ownership(false));

        // When
        var output = await handler.HandleAsync(Command(googleUser.PublicId, Roles.ScopeAdmin));

        // Then — and nothing was written
        Assert.False(output.Success);
        Assert.Contains(GoogleUserMessages.NotAuthorizedToDeleteGoogleUser, output.Errors);
        Assert.False((await googleUsers.GetAllAsync()).Data!.Single().IsDeleted);
    }

    [UnitFact]
    public async Task GivenAlreadyDeletedGoogleUserAndUnauthorizedCaller_WhenHandlingDelete_ThenReturnsNotAuthorizedError()
    {
        // Given a record that is both already deleted and outside the caller's reach. AF-28c must win:
        // if AF-28b answered first, its idempotent 200 would confirm the record exists to a caller
        // who may not know that.
        var googleUsers = new AsyncFakeRepository<GoogleUser>();
        var googleUser = await SeedGoogleUserAsync(googleUsers, isDeleted: true);
        var handler = new DeleteGoogleUserCommandHandler(googleUsers, googleUsers, Ownership(false));

        // When
        var output = await handler.HandleAsync(Command(googleUser.PublicId, Roles.ScopeAdmin));

        // Then
        Assert.False(output.Success);
        Assert.Contains(GoogleUserMessages.NotAuthorizedToDeleteGoogleUser, output.Errors);
    }
}
