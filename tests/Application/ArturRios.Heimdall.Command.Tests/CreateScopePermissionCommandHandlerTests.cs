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

// Unit tests for CreateScopePermissionCommandHandler (UC-31): main flow for a System Admin and for
// an owning Scope Admin, the IncludeAsJwtClaim flag round-trips on the persisted row, plus AF-31a
// (scope missing/deleted), AF-31d (invalid input), and AF-31e (a Scope Admin acting on a scope they
// do not own). A `User` never reaches the handler — [RoleRequirement] refuses them at the endpoint,
// covered in ScopePermissionControllerCreateTests. The ownership rule itself is covered by
// ScopeOwnershipCheckerTests.
public class CreateScopePermissionCommandHandlerTests
{
    private static Mock<IValidator<CreateScopePermissionCommand>> ValidValidator()
    {
        var validator = new Mock<IValidator<CreateScopePermissionCommand>>();
        validator
            .Setup(v => v.ValidateAsync(It.IsAny<CreateScopePermissionCommand>(), It.IsAny<CancellationToken>()))
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

    private static async Task<(AsyncFakeRepository<Scope> scopes, Scope scope)> ScopeStoreAsync(
        bool isDeleted = false)
    {
        var scopes = new AsyncFakeRepository<Scope>();
        var scope = new Scope { PublicId = Guid.NewGuid(), Name = "Acme", IsDeleted = isDeleted };
        await scopes.CreateAsync(scope);
        return (scopes, scope);
    }

    private static CreateScopePermissionCommand Command(
        Guid scopeId, int actingRole, Guid actingPersonId, string name = "billing:read",
        string? description = "Read billing records.", bool includeAsJwtClaim = true) => new()
    {
        ScopeId = scopeId,
        Name = name,
        Description = description,
        IncludeAsJwtClaim = includeAsJwtClaim,
        ActingRole = actingRole,
        ActingPersonId = actingPersonId
    };

    private static CreateScopePermissionCommandHandler Handler(
        AsyncFakeRepository<Scope> scopes,
        AsyncFakeRepository<ScopePermission> permissions,
        IScopeOwnershipChecker? ownership = null,
        Mock<IValidator<CreateScopePermissionCommand>>? validator = null) =>
        new((validator ?? ValidValidator()).Object, scopes, permissions,
            ownership ?? OwnershipChecker());

    [UnitFact]
    public async Task GivenSystemAdminAndValidName_WhenHandlingCreateScopePermission_ThenPermissionIsCreated()
    {
        // Given a SystemAdmin actor (UC-31 main flow)
        var (scopes, scope) = await ScopeStoreAsync();
        var permissions = new AsyncFakeRepository<ScopePermission>();
        var handler = Handler(scopes, permissions);

        // When
        var output = await handler.HandleAsync(
            Command(scope.PublicId, (int)Roles.SystemAdmin, Guid.NewGuid()));

        // Then
        Assert.True(output.Success);
        Assert.Equal("billing:read", output.Data!.Name);
        Assert.Equal("Read billing records.", output.Data.Description);
        Assert.True(output.Data.IncludeAsJwtClaim);
        Assert.Equal(scope.PublicId, output.Data.ScopeId);
        Assert.NotEqual(Guid.Empty, output.Data.Id);
        Assert.Contains(ScopePermissionMessages.ScopePermissionCreatedSuccessfully, output.Messages);
    }

    [UnitFact]
    public async Task GivenOwningScopeAdmin_WhenHandlingCreateScopePermission_ThenPermissionIsCreated()
    {
        // Given a ScopeAdmin who owns the scope (matrix: "owning Scope Admin")
        var (scopes, scope) = await ScopeStoreAsync();
        var permissions = new AsyncFakeRepository<ScopePermission>();
        var caller = Guid.NewGuid();
        var handler = Handler(scopes, permissions, OwnershipChecker(allowed: true));

        // When
        var output = await handler.HandleAsync(
            Command(scope.PublicId, (int)Roles.ScopeAdmin, caller));

        // Then
        Assert.True(output.Success);
        Assert.Single((await permissions.GetAllAsync()).Data!);
    }

    [UnitFact]
    public async Task GivenCreatedPermission_WhenInspectingRow_ThenItCarriesScopeInternalIdAndPublicId()
    {
        // Given internal ids that must never leave the data layer (FR-SP-02: scope fixed at creation)
        var (scopes, scope) = await ScopeStoreAsync();
        var permissions = new AsyncFakeRepository<ScopePermission>();
        var handler = Handler(scopes, permissions);

        // When
        await handler.HandleAsync(
            Command(scope.PublicId, (int)Roles.SystemAdmin, Guid.NewGuid()));

        // Then — the persisted row points at the internal scope id and is not logically deleted
        var stored = (await permissions.GetAllAsync()).Data!.Single();
        Assert.Equal(scope.Id, stored.ScopeId);
        Assert.False(stored.IsDeleted);
        Assert.NotEqual(Guid.Empty, stored.PublicId);
    }

    [UnitFact]
    public async Task GivenIncludeAsJwtClaimFalse_WhenHandlingCreateScopePermission_ThenFlagIsPersistedAsFalse()
    {
        // Given the JWT-claim flag cleared (FR-SP-01: defaults to false, but explicitly sent false)
        var (scopes, scope) = await ScopeStoreAsync();
        var permissions = new AsyncFakeRepository<ScopePermission>();
        var handler = Handler(scopes, permissions);

        // When
        var output = await handler.HandleAsync(
            Command(scope.PublicId, (int)Roles.SystemAdmin, Guid.NewGuid(), includeAsJwtClaim: false));

        // Then — both the response and the stored row carry the flag's value
        Assert.True(output.Success);
        Assert.False(output.Data!.IncludeAsJwtClaim);
        Assert.False((await permissions.GetAllAsync()).Data!.Single().IncludeAsJwtClaim);
    }

    [UnitFact]
    public async Task GivenNullDescription_WhenHandlingCreateScopePermission_ThenPermissionIsCreated()
    {
        // Given no description — the field is optional
        var (scopes, scope) = await ScopeStoreAsync();
        var permissions = new AsyncFakeRepository<ScopePermission>();
        var handler = Handler(scopes, permissions);

        // When
        var output = await handler.HandleAsync(
            Command(scope.PublicId, (int)Roles.SystemAdmin, Guid.NewGuid(), description: null));

        // Then
        Assert.True(output.Success);
        Assert.Null(output.Data!.Description);
        Assert.Null((await permissions.GetAllAsync()).Data!.Single().Description);
    }

    [UnitFact]
    public async Task GivenMissingScope_WhenHandlingCreateScopePermission_ThenScopeNotFoundIsReported()
    {
        // Given an empty scope store (AF-31a)
        var scopes = new AsyncFakeRepository<Scope>();
        var permissions = new AsyncFakeRepository<ScopePermission>();
        var handler = Handler(scopes, permissions);

        // When
        var output = await handler.HandleAsync(
            Command(Guid.NewGuid(), (int)Roles.SystemAdmin, Guid.NewGuid()));

        // Then
        Assert.False(output.Success);
        Assert.Contains(ScopePermissionMessages.ScopeNotFound, output.Errors);
        Assert.Empty((await permissions.GetAllAsync()).Data!);
    }

    [UnitFact]
    public async Task GivenLogicallyDeletedScope_WhenHandlingCreateScopePermission_ThenScopeNotFoundIsReported()
    {
        // Given a logically deleted scope (AF-31a)
        var (scopes, scope) = await ScopeStoreAsync(isDeleted: true);
        var permissions = new AsyncFakeRepository<ScopePermission>();
        var handler = Handler(scopes, permissions);

        // When
        var output = await handler.HandleAsync(
            Command(scope.PublicId, (int)Roles.SystemAdmin, Guid.NewGuid()));

        // Then
        Assert.False(output.Success);
        Assert.Contains(ScopePermissionMessages.ScopeNotFound, output.Errors);
    }

    [UnitFact]
    public async Task GivenScopeAdminWhoDoesNotOwnTheScope_WhenHandlingCreateScopePermission_ThenNotScopeOwnerIsReported()
    {
        // Given the ownership checker rejects the acting ScopeAdmin (AF-31e)
        var (scopes, scope) = await ScopeStoreAsync();
        var permissions = new AsyncFakeRepository<ScopePermission>();
        var handler = Handler(scopes, permissions, OwnershipChecker(allowed: false));

        // When
        var output = await handler.HandleAsync(
            Command(scope.PublicId, (int)Roles.ScopeAdmin, Guid.NewGuid()));

        // Then
        Assert.False(output.Success);
        Assert.Contains(ScopePermissionMessages.NotScopeOwner, output.Errors);
        Assert.Empty((await permissions.GetAllAsync()).Data!);
    }

    [UnitFact]
    public async Task GivenInvalidInput_WhenHandlingCreateScopePermission_ThenNothingIsCreated()
    {
        // Given a validator that rejects the command (AF-31d)
        var (scopes, scope) = await ScopeStoreAsync();
        var permissions = new AsyncFakeRepository<ScopePermission>();
        var validator = new Mock<IValidator<CreateScopePermissionCommand>>();
        validator
            .Setup(v => v.ValidateAsync(It.IsAny<CreateScopePermissionCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidationResult(
                [new ValidationFailure(nameof(CreateScopePermissionCommand.Name),
                    ScopePermissionMessages.NameRequired)]));
        var handler = Handler(scopes, permissions, validator: validator);

        // When
        var output = await handler.HandleAsync(
            Command(scope.PublicId, (int)Roles.SystemAdmin, Guid.NewGuid()));

        // Then
        Assert.False(output.Success);
        Assert.Contains(ScopePermissionMessages.NameRequired, output.Errors);
        Assert.Empty((await permissions.GetAllAsync()).Data!);
    }
}