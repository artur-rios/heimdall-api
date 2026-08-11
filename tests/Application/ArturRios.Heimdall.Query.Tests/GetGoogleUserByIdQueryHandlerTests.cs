using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Query.Handlers;
using ArturRios.Heimdall.Query.Input;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Heimdall.Shared.Services;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Mock;
using Moq;

namespace ArturRios.Heimdall.Query.Tests;

// Unit tests for GetGoogleUserByIdQueryHandler (UC-27, FR-GO-14): the main flow for each of the
// three actors the use case names, the FR-GO-17 exclusion and its IncludeDeleted escape hatch,
// AF-27a (missing, or belonging to another scope), and AF-27b (a Scope Admin who does not own the
// scope, a password User, and a Google User reading somebody else).
//
// The ownership half of the rule is delegated to IScopeOwnershipChecker, which has its own tests in
// Shared.Tests, so it is mocked here and the tests pin what this handler decides: whether to consult
// it at all, and with which scope id.
public class GetGoogleUserByIdQueryHandlerTests
{
    private static readonly Guid ScopePublicId = Guid.NewGuid();

    private const long ScopeInternalId = 42;

    /// <summary>A checker that answers <paramref name="mayManage" /> for every scope.</summary>
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

        // Bogus fills the descriptive fields; the navigation and the flags the handler reads are
        // pinned, since the fake repository resolves Scope.PublicId from the navigation rather than
        // through a join.
        var googleUser = new Bogus.Faker<GoogleUser>()
            .RuleFor(x => x.PublicId, _ => Guid.NewGuid())
            .RuleFor(x => x.GoogleId, _ => $"google-sub-{Guid.NewGuid():N}")
            .RuleFor(x => x.Email, faker => faker.Internet.Email())
            .RuleFor(x => x.IsDeleted, _ => isDeleted)
            .RuleFor(x => x.ScopeId, _ => ScopeInternalId)
            .RuleFor(x => x.Scope, _ => scope)
            .Generate();

        await googleUsers.CreateAsync(googleUser);

        return googleUser;
    }

    private static GetGoogleUserByIdQuery Query(
        Guid id, Guid actingPersonId, Roles actingRole = Roles.SystemAdmin, bool includeDeleted = false) =>
        new()
        {
            ScopeId = ScopePublicId,
            Id = id,
            IncludeDeleted = includeDeleted,
            ActingPersonId = actingPersonId,
            ActingRole = (int)actingRole
        };

    [UnitFact]
    public async Task GivenSystemAdmin_WhenGettingGoogleUserById_ThenReturnsIt()
    {
        // Given a System Admin, who may view any Google User (UC-27 step 2)
        var googleUsers = new AsyncFakeRepository<GoogleUser>();
        var googleUser = await SeedGoogleUserAsync(googleUsers);
        var handler = new GetGoogleUserByIdQueryHandler(googleUsers, Ownership(true));

        // When
        var output = await handler.HandleAsync(Query(googleUser.PublicId, Guid.NewGuid()));

        // Then — every FR-GO-05 field is projected, and no internal id escapes (NFR-15)
        Assert.True(output.Success);
        Assert.Contains(GoogleUserMessages.GoogleUserRetrievedSuccessfully, output.Messages);
        Assert.Equal(googleUser.PublicId, output.Data!.Id);
        Assert.Equal(googleUser.GoogleId, output.Data.GoogleId);
        Assert.Equal(googleUser.Email, output.Data.Email);
        Assert.Equal(ScopePublicId, output.Data.ScopeId);
        Assert.False(output.Data.IsDeleted);
    }

    [UnitFact]
    public async Task GivenOwningScopeAdmin_WhenGettingGoogleUserById_ThenReturnsIt()
    {
        // Given a Scope Admin who owns the scope — the checker answers for the ownership
        var googleUsers = new AsyncFakeRepository<GoogleUser>();
        var googleUser = await SeedGoogleUserAsync(googleUsers);
        var checker = new Mock<IScopeOwnershipChecker>();
        checker
            .Setup(x => x.ActorMayManageScopeAsync(It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<long>()))
            .ReturnsAsync(true);
        var handler = new GetGoogleUserByIdQueryHandler(googleUsers, checker.Object);
        var actingId = Guid.NewGuid();

        // When
        var output = await handler.HandleAsync(Query(googleUser.PublicId, actingId, Roles.ScopeAdmin));

        // Then — and the ownership was asked about the Google User's own scope, by internal id
        Assert.True(output.Success);
        checker.Verify(
            x => x.ActorMayManageScopeAsync((int)Roles.ScopeAdmin, actingId, ScopeInternalId), Times.Once);
    }

    [UnitFact]
    public async Task GivenGoogleUserReadingThemselves_WhenGettingGoogleUserById_ThenReturnsItWithoutConsultingOwnership()
    {
        // Given the Google User themselves — the third actor UC-27 names, who owns no scope at all
        var googleUsers = new AsyncFakeRepository<GoogleUser>();
        var googleUser = await SeedGoogleUserAsync(googleUsers);
        var checker = new Mock<IScopeOwnershipChecker>();
        checker
            .Setup(x => x.ActorMayManageScopeAsync(It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<long>()))
            .ReturnsAsync(false);
        var handler = new GetGoogleUserByIdQueryHandler(googleUsers, checker.Object);

        // When
        var output = await handler.HandleAsync(
            Query(googleUser.PublicId, googleUser.PublicId, Roles.User));

        // Then — the self-read short-circuits, so a Google User is never refused for owning nothing
        Assert.True(output.Success);
        Assert.Equal(googleUser.PublicId, output.Data!.Id);
        checker.Verify(
            x => x.ActorMayManageScopeAsync(It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<long>()), Times.Never);
    }

    [UnitFact]
    public async Task GivenNoSuchGoogleUser_WhenGettingGoogleUserById_ThenReturnsNotFoundError()
    {
        // Given an id nobody holds (AF-27a)
        var googleUsers = new AsyncFakeRepository<GoogleUser>();
        await SeedGoogleUserAsync(googleUsers);
        var handler = new GetGoogleUserByIdQueryHandler(googleUsers, Ownership(true));

        // When
        var output = await handler.HandleAsync(Query(Guid.NewGuid(), Guid.NewGuid()));

        // Then
        Assert.False(output.Success);
        Assert.Contains(GoogleUserMessages.GoogleUserNotFound, output.Errors);
    }

    [UnitFact]
    public async Task GivenGoogleUserInAnotherScope_WhenGettingGoogleUserById_ThenReturnsNotFoundError()
    {
        // Given a Google User that exists, but not under the scope the route addresses — it is not
        // the resource this path names, so it is AF-27a rather than AF-27b
        var googleUsers = new AsyncFakeRepository<GoogleUser>();
        var googleUser = await SeedGoogleUserAsync(googleUsers, scopePublicId: Guid.NewGuid());
        var handler = new GetGoogleUserByIdQueryHandler(googleUsers, Ownership(true));

        // When
        var output = await handler.HandleAsync(Query(googleUser.PublicId, Guid.NewGuid()));

        // Then
        Assert.False(output.Success);
        Assert.Contains(GoogleUserMessages.GoogleUserNotFound, output.Errors);
    }

    [UnitFact]
    public async Task GivenLogicallyDeletedGoogleUser_WhenGettingGoogleUserById_ThenReturnsNotFoundError()
    {
        // Given a logically deleted Google User and a default read (FR-GO-17, AF-27a)
        var googleUsers = new AsyncFakeRepository<GoogleUser>();
        var googleUser = await SeedGoogleUserAsync(googleUsers, isDeleted: true);
        var handler = new GetGoogleUserByIdQueryHandler(googleUsers, Ownership(true));

        // When
        var output = await handler.HandleAsync(Query(googleUser.PublicId, Guid.NewGuid()));

        // Then
        Assert.False(output.Success);
        Assert.Contains(GoogleUserMessages.GoogleUserNotFound, output.Errors);
    }

    [UnitFact]
    public async Task GivenLogicallyDeletedGoogleUserAndIncludeDeleted_WhenGettingGoogleUserById_ThenReturnsIt()
    {
        // Given the same record, explicitly requested (FR-GO-17's escape hatch)
        var googleUsers = new AsyncFakeRepository<GoogleUser>();
        var googleUser = await SeedGoogleUserAsync(googleUsers, isDeleted: true);
        var handler = new GetGoogleUserByIdQueryHandler(googleUsers, Ownership(true));

        // When
        var output = await handler.HandleAsync(
            Query(googleUser.PublicId, Guid.NewGuid(), includeDeleted: true));

        // Then
        Assert.True(output.Success);
        Assert.True(output.Data!.IsDeleted);
    }

    [UnitFact]
    public async Task GivenNonOwningScopeAdmin_WhenGettingGoogleUserById_ThenReturnsNotAuthorizedError()
    {
        // Given a Scope Admin who does not own the Google User's scope (AF-27b)
        var googleUsers = new AsyncFakeRepository<GoogleUser>();
        var googleUser = await SeedGoogleUserAsync(googleUsers);
        var handler = new GetGoogleUserByIdQueryHandler(googleUsers, Ownership(false));

        // When
        var output = await handler.HandleAsync(
            Query(googleUser.PublicId, Guid.NewGuid(), Roles.ScopeAdmin));

        // Then
        Assert.False(output.Success);
        Assert.Contains(GoogleUserMessages.NotAuthorizedToViewGoogleUser, output.Errors);
    }

    [UnitFact]
    public async Task GivenAnotherUser_WhenGettingGoogleUserById_ThenReturnsNotAuthorizedError()
    {
        // Given a User who is not this Google User — a password person, or another Google User in
        // the same scope. The matrix grants a User this read only as self (AF-27b).
        var googleUsers = new AsyncFakeRepository<GoogleUser>();
        var googleUser = await SeedGoogleUserAsync(googleUsers);
        var handler = new GetGoogleUserByIdQueryHandler(googleUsers, Ownership(false));

        // When
        var output = await handler.HandleAsync(Query(googleUser.PublicId, Guid.NewGuid(), Roles.User));

        // Then
        Assert.False(output.Success);
        Assert.Contains(GoogleUserMessages.NotAuthorizedToViewGoogleUser, output.Errors);
    }
}
