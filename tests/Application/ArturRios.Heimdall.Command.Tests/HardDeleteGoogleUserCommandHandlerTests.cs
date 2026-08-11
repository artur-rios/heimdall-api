using ArturRios.Heimdall.Command.Handlers;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Mock;

namespace ArturRios.Heimdall.Command.Tests;

// Unit tests for HardDeleteGoogleUserCommandHandler (UC-29, FR-GO-16): the main flow, the logically
// deleted record a cleanup pass starts from, and every shape of AF-29a — unknown id, a Google User
// belonging to another scope, and a repeated call.
//
// There is no authorization test here: UC-29's only actor is the System Admin and the endpoint's
// RoleRequirement settles it, so the command carries no acting person and the handler applies no
// rule. GoogleUserControllerHardDeleteTests proves the endpoint enforces it.
public class HardDeleteGoogleUserCommandHandlerTests
{
    private static readonly Guid ScopePublicId = Guid.NewGuid();

    private static async Task<GoogleUser> SeedGoogleUserAsync(
        AsyncFakeRepository<GoogleUser> googleUsers,
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
        var googleUsers = new AsyncFakeRepository<GoogleUser>();
        var googleUser = await SeedGoogleUserAsync(googleUsers);
        var handler = new HardDeleteGoogleUserCommandHandler(googleUsers, googleUsers);

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
        var googleUsers = new AsyncFakeRepository<GoogleUser>();
        var googleUser = await SeedGoogleUserAsync(googleUsers, isDeleted: true);
        var handler = new HardDeleteGoogleUserCommandHandler(googleUsers, googleUsers);

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
        var googleUsers = new AsyncFakeRepository<GoogleUser>();
        await SeedGoogleUserAsync(googleUsers);
        var handler = new HardDeleteGoogleUserCommandHandler(googleUsers, googleUsers);

        // When
        var output = await handler.HandleAsync(Command(Guid.NewGuid()));

        // Then — and the Google User that does exist was left alone
        Assert.False(output.Success);
        Assert.Contains(GoogleUserMessages.GoogleUserNotFound, output.Errors);
        Assert.Single((await googleUsers.GetAllAsync()).Data!);
    }

    [UnitFact]
    public async Task GivenGoogleUserInAnotherScope_WhenHandlingHardDelete_ThenReturnsNotFoundError()
    {
        // Given a Google User that exists but belongs to another scope — not the resource this path
        // addresses (AF-29a)
        var googleUsers = new AsyncFakeRepository<GoogleUser>();
        var googleUser = await SeedGoogleUserAsync(googleUsers, scopePublicId: Guid.NewGuid());
        var handler = new HardDeleteGoogleUserCommandHandler(googleUsers, googleUsers);

        // When
        var output = await handler.HandleAsync(Command(googleUser.PublicId));

        // Then — refused, and untouched: a wrong-scope path must not destroy anything
        Assert.False(output.Success);
        Assert.Contains(GoogleUserMessages.GoogleUserNotFound, output.Errors);
        Assert.Single((await googleUsers.GetAllAsync()).Data!);
    }

    [UnitFact]
    public async Task GivenAlreadyHardDeletedGoogleUser_WhenHandlingHardDeleteAgain_ThenReturnsNotFoundError()
    {
        // Given a second call for a record the first one removed. UC-29 defines no idempotent path,
        // unlike UC-28's AF-28b, so the repeat is AF-29a.
        var googleUsers = new AsyncFakeRepository<GoogleUser>();
        var googleUser = await SeedGoogleUserAsync(googleUsers);
        var handler = new HardDeleteGoogleUserCommandHandler(googleUsers, googleUsers);
        await handler.HandleAsync(Command(googleUser.PublicId));

        // When
        var output = await handler.HandleAsync(Command(googleUser.PublicId));

        // Then
        Assert.False(output.Success);
        Assert.Contains(GoogleUserMessages.GoogleUserNotFound, output.Errors);
    }
}
