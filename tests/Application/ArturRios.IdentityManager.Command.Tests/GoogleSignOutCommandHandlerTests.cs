using ArturRios.IdentityManager.Command.Handlers;
using ArturRios.IdentityManager.Command.Input;
using ArturRios.IdentityManager.Domain.Entities;
using ArturRios.IdentityManager.Shared.Messages;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Mock;

namespace ArturRios.IdentityManager.Command.Tests;

// Unit tests for GoogleSignOutCommandHandler (UC-26, FR-GO-18): the main flow — the caller's token
// names a live Google User, so the sign-out is acknowledged — and AF-26a, which answers alike for
// every caller whose token does not name one: a Google User that UC-28 logically deleted, one that
// UC-29 removed outright, and a password User or administrator whose id was never a Google User's.
// The end-to-end behavior, including the missing-token half of AF-26a that the auth middleware
// answers, is covered by AuthControllerGoogleSignOutTests.
public class GoogleSignOutCommandHandlerTests
{
    private static async Task<GoogleUser> SeedGoogleUserAsync(
        AsyncFakeRepository<GoogleUser> googleUsers, bool isDeleted = false)
    {
        // Bogus fills the descriptive fields; only what the lookup reads is pinned.
        var googleUser = new Bogus.Faker<GoogleUser>()
            .RuleFor(x => x.PublicId, _ => Guid.NewGuid())
            .RuleFor(x => x.GoogleId, _ => $"google-sub-{Guid.NewGuid():N}")
            .RuleFor(x => x.IsDeleted, _ => isDeleted)
            .Generate();

        await googleUsers.CreateAsync(googleUser);

        return googleUser;
    }

    private static GoogleSignOutCommand Command(Guid actingGoogleUserId) =>
        new() { ActingPersonId = actingGoogleUserId };

    [UnitFact]
    public async Task GivenActiveGoogleUser_WhenHandlingGoogleSignOut_ThenSucceeds()
    {
        // Given a token naming a Google User in good standing (UC-26 main flow)
        var googleUsers = new AsyncFakeRepository<GoogleUser>();
        var googleUser = await SeedGoogleUserAsync(googleUsers);
        var handler = new GoogleSignOutCommandHandler(googleUsers);

        // When
        var output = await handler.HandleAsync(Command(googleUser.PublicId));

        // Then
        Assert.True(output.Success);
        Assert.Contains(AuthMessages.GoogleSignOutSuccessful, output.Messages);
    }

    [UnitFact]
    public async Task GivenNoMatchingGoogleUser_WhenHandlingGoogleSignOut_ThenAuthenticationFails()
    {
        // Given a token whose id names no Google User at all — a password User's, an
        // administrator's, or one whose Google User UC-29 hard deleted (AF-26a)
        var googleUsers = new AsyncFakeRepository<GoogleUser>();
        await SeedGoogleUserAsync(googleUsers);
        var handler = new GoogleSignOutCommandHandler(googleUsers);

        // When
        var output = await handler.HandleAsync(Command(Guid.NewGuid()));

        // Then
        Assert.False(output.Success);
        Assert.Contains(AuthMessages.GoogleAuthenticationFailed, output.Errors);
    }

    [UnitFact]
    public async Task GivenLogicallyDeletedGoogleUser_WhenHandlingGoogleSignOut_ThenAuthenticationFails()
    {
        // Given the Google User the token names was logically deleted by UC-28, so UC-25 would no
        // longer authenticate it (AF-25d) and the token is no longer one UC-26's precondition
        // recognises (AF-26a)
        var googleUsers = new AsyncFakeRepository<GoogleUser>();
        var googleUser = await SeedGoogleUserAsync(googleUsers, isDeleted: true);
        var handler = new GoogleSignOutCommandHandler(googleUsers);

        // When
        var output = await handler.HandleAsync(Command(googleUser.PublicId));

        // Then — the same message the unknown-caller flow gives, so neither answer reveals whether
        // the account exists
        Assert.False(output.Success);
        Assert.Contains(AuthMessages.GoogleAuthenticationFailed, output.Errors);
    }

    [UnitFact]
    public async Task GivenSignOut_WhenHandlingGoogleSignOut_ThenLeavesTheGoogleUserUntouched()
    {
        // Given a live Google User (UC-26 step 2: under this project's stateless token strategy the
        // sign-out writes nothing — the client discards the token)
        var googleUsers = new AsyncFakeRepository<GoogleUser>();
        var googleUser = await SeedGoogleUserAsync(googleUsers);

        var handler = new GoogleSignOutCommandHandler(googleUsers);

        // When
        await handler.HandleAsync(Command(googleUser.PublicId));

        // Then — the record is exactly as it was; signing out is not a deletion
        var stored = (await googleUsers.GetAllAsync()).Data!.Single();
        Assert.Equal(googleUser.PublicId, stored.PublicId);
        Assert.False(stored.IsDeleted);
    }
}
