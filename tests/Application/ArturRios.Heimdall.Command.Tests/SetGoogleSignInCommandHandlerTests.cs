using ArturRios.Heimdall.Command.Handlers;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Heimdall.Shared.Services;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Mock;
using FluentValidation;
using FluentValidation.Results;
using Moq;

namespace ArturRios.Heimdall.Command.Tests;

// Unit tests for SetGoogleSignInCommandHandler (UC-24): the main flow writing the flag in both
// directions and for both actors, AF-24a (scope missing or logically deleted), AF-24b delegation (the
// checker rejects the actor), and AF-24c (an omitted `enabled`, which must not silently disable the
// setting). The AF-24b ownership rule itself is covered by ScopeOwnershipCheckerTests;
// the 401/403-by-attribute flows are covered by ScopeControllerSetGoogleSignInTests.
public class SetGoogleSignInCommandHandlerTests
{
    private static readonly DateTime SeededUpdatedAt = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static Mock<IValidator<SetGoogleSignInCommand>> ValidValidator()
    {
        var validator = new Mock<IValidator<SetGoogleSignInCommand>>();
        validator
            .Setup(v => v.ValidateAsync(It.IsAny<SetGoogleSignInCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidationResult());
        return validator;
    }

    private static IScopeOwnershipChecker OwnershipChecker(bool allowed = true)
    {
        var checker = new Mock<IScopeOwnershipChecker>();
        checker
            .Setup(c => c.ActorMayManageScopeAsync(It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<long>()))
            .ReturnsAsync(allowed);
        return checker.Object;
    }

    private static async Task<Scope> SeedScopeAsync(
        AsyncFakeRepository<Scope> scopes,
        bool googleSignInEnabled = false,
        bool isDeleted = false,
        Guid? ownerPublicId = null)
    {
        // Bogus builds the owner person; only the field the projection reads is pinned.
        var owner = new Bogus.Faker<Person>()
            .RuleFor(p => p.PublicId, _ => ownerPublicId ?? Guid.NewGuid())
            .Generate();

        var scope = new Scope
        {
            PublicId = Guid.NewGuid(),
            Name = $"scope-{Guid.NewGuid():N}",
            Description = "A scope",
            GoogleSignInEnabled = googleSignInEnabled,
            IsDeleted = isDeleted,
            CreatedAt = SeededUpdatedAt,
            UpdatedAt = SeededUpdatedAt,
            Owners = [new ScopeOwner { Person = owner }]
        };

        await scopes.CreateAsync(scope);

        return scope;
    }

    private static SetGoogleSignInCommand Command(
        Guid scopeId, bool? enabled = true, Roles actingRole = Roles.SystemAdmin, Guid? actingPersonId = null) =>
        new()
        {
            Id = scopeId,
            Enabled = enabled,
            ActingRole = (int)actingRole,
            ActingPersonId = actingPersonId ?? Guid.NewGuid()
        };

    private static SetGoogleSignInCommandHandler Handler(
        AsyncFakeRepository<Scope> scopes,
        bool allowed = true,
        Mock<IValidator<SetGoogleSignInCommand>>? validator = null) =>
        new((validator ?? ValidValidator()).Object, scopes, scopes, OwnershipChecker(allowed));

    private static async Task<Scope> StoredAsync(AsyncFakeRepository<Scope> scopes, Scope scope) =>
        (await scopes.GetAllAsync()).Data!.Single(x => x.PublicId == scope.PublicId);

    [UnitFact]
    public async Task GivenSystemAdminAndEnabledTrue_WhenHandlingSetGoogleSignIn_ThenFlagIsEnabled()
    {
        // Given a scope with Google Sign-In off (UC-24 main flow, FR-GO-01)
        var scopes = new AsyncFakeRepository<Scope>();
        var scope = await SeedScopeAsync(scopes);
        var handler = Handler(scopes);

        // When
        var output = await handler.HandleAsync(Command(scope.PublicId, enabled: true));

        // Then — output
        Assert.True(output.Success);
        Assert.True(output.Data!.GoogleSignInEnabled);
        Assert.Contains(ScopeMessages.GoogleSignInUpdatedSuccessfully, output.Messages);

        // Then — persisted state
        Assert.True((await StoredAsync(scopes, scope)).GoogleSignInEnabled);
    }

    [UnitFact]
    public async Task GivenSystemAdminAndEnabledFalse_WhenHandlingSetGoogleSignIn_ThenFlagIsDisabled()
    {
        // Given a scope with Google Sign-In already on — the "Disable" half of UC-24
        var scopes = new AsyncFakeRepository<Scope>();
        var scope = await SeedScopeAsync(scopes, googleSignInEnabled: true);
        var handler = Handler(scopes);

        // When
        var output = await handler.HandleAsync(Command(scope.PublicId, enabled: false));

        // Then
        Assert.True(output.Success);
        Assert.False(output.Data!.GoogleSignInEnabled);
        Assert.False((await StoredAsync(scopes, scope)).GoogleSignInEnabled);
    }

    [UnitFact]
    public async Task GivenExistingOwnerActor_WhenHandlingSetGoogleSignIn_ThenFlagIsUpdated()
    {
        // Given a Scope Admin actor the checker accepts as an owner of the scope (FR-GO-02)
        var scopes = new AsyncFakeRepository<Scope>();
        var scope = await SeedScopeAsync(scopes);
        var handler = Handler(scopes);

        // When
        var output = await handler.HandleAsync(
            Command(scope.PublicId, enabled: true, Roles.ScopeAdmin, Guid.NewGuid()));

        // Then
        Assert.True(output.Success);
        Assert.Contains(ScopeMessages.GoogleSignInUpdatedSuccessfully, output.Messages);
        Assert.True((await StoredAsync(scopes, scope)).GoogleSignInEnabled);
    }

    [UnitFact]
    public async Task GivenScope_WhenHandlingSetGoogleSignIn_ThenUpdatedAtIsStamped()
    {
        // Given a scope whose UpdatedAt is in the past (no DB trigger maintains it)
        var scopes = new AsyncFakeRepository<Scope>();
        var scope = await SeedScopeAsync(scopes);
        var handler = Handler(scopes);

        // When
        var output = await handler.HandleAsync(Command(scope.PublicId));

        // Then
        Assert.True(output.Success);
        Assert.True(output.Data!.UpdatedAt > SeededUpdatedAt);
        Assert.Equal(SeededUpdatedAt, output.Data.CreatedAt);
        Assert.True((await StoredAsync(scopes, scope)).UpdatedAt > SeededUpdatedAt);
    }

    [UnitFact]
    public async Task GivenOutput_WhenHandlingSetGoogleSignIn_ThenItCarriesTheScopeWithPublicIdentifiersOnly()
    {
        // Given a scope with a known owner (UC-24 step 6 returns the scope, SRD §4.0 / NFR-15)
        var scopes = new AsyncFakeRepository<Scope>();
        var ownerPublicId = Guid.NewGuid();
        var scope = await SeedScopeAsync(scopes, ownerPublicId: ownerPublicId);
        var handler = Handler(scopes);

        // When
        var output = await handler.HandleAsync(Command(scope.PublicId));

        // Then
        Assert.True(output.Success);
        Assert.Equal(scope.PublicId, output.Data!.Id);
        Assert.Equal(scope.Name, output.Data.Name);
        Assert.Equal("A scope", output.Data.Description);
        Assert.Equal([ownerPublicId], output.Data.OwnerIds);
    }

    [UnitFact]
    public async Task GivenFlagAlreadyAtRequestedValue_WhenHandlingSetGoogleSignIn_ThenRequestSucceedsAndFlagIsUnchanged()
    {
        // Given a scope already enabled, asked to enable again — PUT is idempotent and UC-24 defines
        // no alternative flow for it, so it is the plain main flow
        var scopes = new AsyncFakeRepository<Scope>();
        var scope = await SeedScopeAsync(scopes, googleSignInEnabled: true);
        var handler = Handler(scopes);

        // When
        var output = await handler.HandleAsync(Command(scope.PublicId, enabled: true));

        // Then
        Assert.True(output.Success);
        Assert.True(output.Data!.GoogleSignInEnabled);
        Assert.Contains(ScopeMessages.GoogleSignInUpdatedSuccessfully, output.Messages);
        Assert.True((await StoredAsync(scopes, scope)).GoogleSignInEnabled);
    }

    [UnitFact]
    public async Task GivenUnknownScope_WhenHandlingSetGoogleSignIn_ThenScopeNotFoundIsReported()
    {
        // Given an empty store (AF-24a)
        var scopes = new AsyncFakeRepository<Scope>();
        var handler = Handler(scopes);

        // When
        var output = await handler.HandleAsync(Command(Guid.NewGuid()));

        // Then
        Assert.False(output.Success);
        Assert.Contains(ScopeMessages.ScopeNotFound, output.Errors);
    }

    [UnitFact]
    public async Task GivenLogicallyDeletedScope_WhenHandlingSetGoogleSignIn_ThenScopeNotFoundIsReported()
    {
        // Given a scope withdrawn from service (AF-24a) — enabling it could never take effect anyway,
        // FR-GO-13 refuses Google sign-in for a deleted scope
        var scopes = new AsyncFakeRepository<Scope>();
        var scope = await SeedScopeAsync(scopes, isDeleted: true);
        var handler = Handler(scopes);

        // When
        var output = await handler.HandleAsync(Command(scope.PublicId, enabled: true));

        // Then
        Assert.False(output.Success);
        Assert.Contains(ScopeMessages.ScopeNotFound, output.Errors);

        var stored = await StoredAsync(scopes, scope);
        Assert.False(stored.GoogleSignInEnabled);
        Assert.Equal(SeededUpdatedAt, stored.UpdatedAt);
    }

    [UnitFact]
    public async Task GivenScopeAdminNotOwningTheScope_WhenHandlingSetGoogleSignIn_ThenNotScopeOwnerIsReported()
    {
        // Given a Scope Admin the ownership checker rejects (AF-24b)
        var scopes = new AsyncFakeRepository<Scope>();
        var scope = await SeedScopeAsync(scopes);
        var handler = Handler(scopes, allowed: false);

        // When
        var output = await handler.HandleAsync(
            Command(scope.PublicId, enabled: true, Roles.ScopeAdmin, Guid.NewGuid()));

        // Then
        Assert.False(output.Success);
        Assert.Contains(ScopeMessages.NotScopeOwner, output.Errors);

        var stored = await StoredAsync(scopes, scope);
        Assert.False(stored.GoogleSignInEnabled);
        Assert.Equal(SeededUpdatedAt, stored.UpdatedAt);
    }

    [UnitFact]
    public async Task GivenEnabledNotSupplied_WhenHandlingSetGoogleSignIn_ThenEnabledRequiredIsReported()
    {
        // Given a validator reporting the AF-24c failure: without it, a body omitting `enabled` would
        // bind to false and silently disable the setting (NFR-10)
        var validator = new Mock<IValidator<SetGoogleSignInCommand>>();
        validator
            .Setup(v => v.ValidateAsync(It.IsAny<SetGoogleSignInCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidationResult([new ValidationFailure("Enabled", ScopeMessages.EnabledRequired)]));
        var scopes = new AsyncFakeRepository<Scope>();
        var scope = await SeedScopeAsync(scopes, googleSignInEnabled: true);
        var handler = Handler(scopes, validator: validator);

        // When
        var output = await handler.HandleAsync(Command(scope.PublicId, enabled: null));

        // Then
        Assert.False(output.Success);
        Assert.Contains(ScopeMessages.EnabledRequired, output.Errors);

        var stored = await StoredAsync(scopes, scope);
        Assert.True(stored.GoogleSignInEnabled);
        Assert.Equal(SeededUpdatedAt, stored.UpdatedAt);
    }
}
