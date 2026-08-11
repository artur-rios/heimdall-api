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

// Unit tests for UpdateScopePermissionCommandHandler (UC-33): main flow for a System Admin and for
// an owning Scope Admin, the IncludeAsJwtClaim flag round-trips, plus AF-33a (permission missing,
// in another scope, addressed through an unknown scope, or logically deleted), AF-33e (an actor who
// does not own the scope), and step 2's input validation. A `User` never reaches the handler —
// [RoleRequirement] refuses them at the endpoint, covered in ScopePermissionControllerUpdateTests.
public class UpdateScopePermissionCommandHandlerTests
{
    private const string NewName = "billing:write";
    private const string NewDescription = "Write billing records.";
    private const bool NewIncludeAsJwtClaim = false;

    private static Mock<IValidator<UpdateScopePermissionCommand>> ValidValidator()
    {
        var validator = new Mock<IValidator<UpdateScopePermissionCommand>>();
        validator
            .Setup(v => v.ValidateAsync(It.IsAny<UpdateScopePermissionCommand>(), It.IsAny<CancellationToken>()))
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

    private static async Task<Scope> SeedScopeAsync(AsyncFakeRepository<Scope> scopes, string name = "Acme")
    {
        var scope = new Scope { PublicId = Guid.NewGuid(), Name = name };
        await scopes.CreateAsync(scope);
        return scope;
    }

    private static async Task<ScopePermission> SeedPermissionAsync(
        AsyncFakeRepository<ScopePermission> permissions, Scope scope, bool isDeleted = false)
    {
        var permission = new ScopePermission
        {
            PublicId = Guid.NewGuid(),
            Name = "billing:read",
            Description = "Read billing records.",
            IncludeAsJwtClaim = true,
            IsDeleted = isDeleted,
            ScopeId = scope.Id,
            Scope = scope
        };
        await permissions.CreateAsync(permission);
        return permission;
    }

    private static UpdateScopePermissionCommand Command(
        Guid scopeId, Guid id, int actingRole, Guid actingPersonId,
        string name = NewName, string? description = NewDescription,
        bool includeAsJwtClaim = NewIncludeAsJwtClaim) => new()
    {
        ScopeId = scopeId,
        Id = id,
        Name = name,
        Description = description,
        IncludeAsJwtClaim = includeAsJwtClaim,
        ActingRole = actingRole,
        ActingPersonId = actingPersonId
    };

    private static UpdateScopePermissionCommandHandler Handler(
        AsyncFakeRepository<ScopePermission> permissions,
        IScopeOwnershipChecker? ownership = null,
        Mock<IValidator<UpdateScopePermissionCommand>>? validator = null) =>
        new((validator ?? ValidValidator()).Object, permissions, permissions, ownership ?? OwnershipChecker());

    private static async Task<ScopePermission> StoredAsync(
        AsyncFakeRepository<ScopePermission> permissions, Guid publicId) =>
        (await permissions.GetAllAsync()).Data!.Single(p => p.PublicId == publicId);

    [UnitFact]
    public async Task GivenSystemAdmin_WhenHandlingUpdateScopePermission_ThenPermissionIsUpdated()
    {
        // Given a permission a SystemAdmin renames, re-describes, and clears the JWT flag on
        var scopes = new AsyncFakeRepository<Scope>();
        var permissions = new AsyncFakeRepository<ScopePermission>();
        var scope = await SeedScopeAsync(scopes);
        var permission = await SeedPermissionAsync(permissions, scope);
        var handler = Handler(permissions);

        // When
        var output = await handler.HandleAsync(Command(
            scope.PublicId, permission.PublicId, (int)Roles.SystemAdmin, Guid.NewGuid()));

        // Then
        Assert.True(output.Success);
        Assert.Equal(NewName, output.Data!.Name);
        Assert.Equal(NewDescription, output.Data.Description);
        Assert.False(output.Data.IncludeAsJwtClaim);
        Assert.Contains(ScopePermissionMessages.ScopePermissionUpdatedSuccessfully, output.Messages);

        var stored = await StoredAsync(permissions, permission.PublicId);
        Assert.Equal(NewName, stored.Name);
        Assert.Equal(NewDescription, stored.Description);
        Assert.False(stored.IncludeAsJwtClaim);
    }

    [UnitFact]
    public async Task GivenOwningScopeAdmin_WhenHandlingUpdateScopePermission_ThenPermissionIsUpdated()
    {
        // Given a ScopeAdmin who owns the scope updating the permission (UC-33 step 3)
        var scopes = new AsyncFakeRepository<Scope>();
        var permissions = new AsyncFakeRepository<ScopePermission>();
        var scope = await SeedScopeAsync(scopes);
        var permission = await SeedPermissionAsync(permissions, scope);
        var caller = Guid.NewGuid();
        var handler = Handler(permissions, OwnershipChecker(allowed: true));

        // When
        var output = await handler.HandleAsync(
            Command(scope.PublicId, permission.PublicId, (int)Roles.ScopeAdmin, caller));

        // Then
        Assert.True(output.Success);
        Assert.Equal(NewName, output.Data!.Name);
    }

    [UnitFact]
    public async Task GivenUpdatedPermission_WhenInspectingRow_ThenUpdatedAtIsStampedAndCreatedAtIsNot()
    {
        // Given an existing permission (UC-33 step 5: no DB trigger maintains UpdatedAt)
        var scopes = new AsyncFakeRepository<Scope>();
        var permissions = new AsyncFakeRepository<ScopePermission>();
        var scope = await SeedScopeAsync(scopes);
        var permission = await SeedPermissionAsync(permissions, scope);
        var createdAt = permission.CreatedAt;
        var before = DateTime.UtcNow;
        var handler = Handler(permissions);

        // When
        var output = await handler.HandleAsync(Command(
            scope.PublicId, permission.PublicId, (int)Roles.SystemAdmin, Guid.NewGuid()));

        // Then
        Assert.True(output.Data!.UpdatedAt >= before);
        Assert.Equal(createdAt, output.Data.CreatedAt);
        Assert.Equal(createdAt, (await StoredAsync(permissions, permission.PublicId)).CreatedAt);
    }

    [UnitFact]
    public async Task GivenOutput_WhenInspectingFields_ThenItCarriesPublicIdentifiersOnly()
    {
        // Given internal ids that must never leave the data layer (SRD §4.0)
        var scopes = new AsyncFakeRepository<Scope>();
        var permissions = new AsyncFakeRepository<ScopePermission>();
        var scope = await SeedScopeAsync(scopes);
        var permission = await SeedPermissionAsync(permissions, scope);
        var handler = Handler(permissions);

        // When
        var output = await handler.HandleAsync(Command(
            scope.PublicId, permission.PublicId, (int)Roles.SystemAdmin, Guid.NewGuid()));

        // Then — only public identifiers appear (the permission's and the scope's PublicIds)
        Assert.Equal(permission.PublicId, output.Data!.Id);
        Assert.Equal(scope.PublicId, output.Data.ScopeId);
        Assert.NotEqual(Guid.Empty, output.Data.Id);
    }

    [UnitFact]
    public async Task GivenUnknownPermission_WhenHandlingUpdateScopePermission_ThenNotFoundIsReported()
    {
        // Given a permission id nobody holds (AF-33a)
        var scopes = new AsyncFakeRepository<Scope>();
        var permissions = new AsyncFakeRepository<ScopePermission>();
        var scope = await SeedScopeAsync(scopes);
        var handler = Handler(permissions);

        // When
        var output = await handler.HandleAsync(Command(
            scope.PublicId, Guid.NewGuid(), (int)Roles.SystemAdmin, Guid.NewGuid()));

        // Then
        Assert.False(output.Success);
        Assert.Contains(ScopePermissionMessages.ScopePermissionNotFound, output.Errors);
    }

    [UnitFact]
    public async Task GivenPermissionOfADifferentScope_WhenHandlingUpdateScopePermission_ThenNotFoundIsReported()
    {
        // Given a permission addressed through a scope it does not belong to (AF-33a)
        var scopes = new AsyncFakeRepository<Scope>();
        var permissions = new AsyncFakeRepository<ScopePermission>();
        var scope = await SeedScopeAsync(scopes);
        var otherScope = await SeedScopeAsync(scopes, "Other");
        var permission = await SeedPermissionAsync(permissions, otherScope);
        var handler = Handler(permissions);

        // When
        var output = await handler.HandleAsync(Command(
            scope.PublicId, permission.PublicId, (int)Roles.SystemAdmin, Guid.NewGuid()));

        // Then — and the row keeps its original attributes
        Assert.False(output.Success);
        Assert.Contains(ScopePermissionMessages.ScopePermissionNotFound, output.Errors);
        Assert.Equal("billing:read", (await StoredAsync(permissions, permission.PublicId)).Name);
    }

    [UnitFact]
    public async Task GivenUnknownScope_WhenHandlingUpdateScopePermission_ThenNotFoundIsReported()
    {
        // Given a scope id nobody holds (AF-33a)
        var scopes = new AsyncFakeRepository<Scope>();
        var permissions = new AsyncFakeRepository<ScopePermission>();
        var scope = await SeedScopeAsync(scopes);
        var permission = await SeedPermissionAsync(permissions, scope);
        var handler = Handler(permissions);

        // When
        var output = await handler.HandleAsync(Command(
            Guid.NewGuid(), permission.PublicId, (int)Roles.SystemAdmin, Guid.NewGuid()));

        // Then
        Assert.False(output.Success);
        Assert.Contains(ScopePermissionMessages.ScopePermissionNotFound, output.Errors);
    }

    [UnitFact]
    public async Task GivenLogicallyDeletedPermission_WhenHandlingUpdateScopePermission_ThenNotFoundIsReported()
    {
        // Given a logically deleted permission: the precondition excludes it (AF-33a)
        var scopes = new AsyncFakeRepository<Scope>();
        var permissions = new AsyncFakeRepository<ScopePermission>();
        var scope = await SeedScopeAsync(scopes);
        var permission = await SeedPermissionAsync(permissions, scope, isDeleted: true);
        var handler = Handler(permissions);

        // When
        var output = await handler.HandleAsync(Command(
            scope.PublicId, permission.PublicId, (int)Roles.SystemAdmin, Guid.NewGuid()));

        // Then
        Assert.False(output.Success);
        Assert.Contains(ScopePermissionMessages.ScopePermissionNotFound, output.Errors);
        Assert.Equal("billing:read", (await StoredAsync(permissions, permission.PublicId)).Name);
    }

    [UnitFact]
    public async Task GivenNonOwningScopeAdmin_WhenHandlingUpdateScopePermission_ThenNotScopeOwnerIsReported()
    {
        // Given the ownership checker rejects the acting ScopeAdmin (AF-33e)
        var scopes = new AsyncFakeRepository<Scope>();
        var permissions = new AsyncFakeRepository<ScopePermission>();
        var scope = await SeedScopeAsync(scopes);
        var permission = await SeedPermissionAsync(permissions, scope);
        var handler = Handler(permissions, OwnershipChecker(allowed: false));

        // When
        var output = await handler.HandleAsync(
            Command(scope.PublicId, permission.PublicId, (int)Roles.ScopeAdmin, Guid.NewGuid()));

        // Then
        Assert.False(output.Success);
        Assert.Contains(ScopePermissionMessages.NotScopeOwner, output.Errors);
        Assert.Equal("billing:read", (await StoredAsync(permissions, permission.PublicId)).Name);
    }

    [UnitFact]
    public async Task GivenInvalidInput_WhenHandlingUpdateScopePermission_ThenNothingIsChanged()
    {
        // Given a validator that rejects the command (UC-33 step 2)
        var scopes = new AsyncFakeRepository<Scope>();
        var permissions = new AsyncFakeRepository<ScopePermission>();
        var scope = await SeedScopeAsync(scopes);
        var permission = await SeedPermissionAsync(permissions, scope);
        var validator = new Mock<IValidator<UpdateScopePermissionCommand>>();
        validator
            .Setup(v => v.ValidateAsync(It.IsAny<UpdateScopePermissionCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidationResult(
                [new ValidationFailure(nameof(UpdateScopePermissionCommand.Name),
                    ScopePermissionMessages.NameRequired)]));
        var handler = Handler(permissions, validator: validator);

        // When
        var output = await handler.HandleAsync(Command(
            scope.PublicId, permission.PublicId, (int)Roles.SystemAdmin, Guid.NewGuid(),
            name: string.Empty));

        // Then
        Assert.False(output.Success);
        Assert.Contains(ScopePermissionMessages.NameRequired, output.Errors);
        Assert.Equal("billing:read", (await StoredAsync(permissions, permission.PublicId)).Name);
    }

    [UnitFact]
    public async Task GivenAuthorizationRunsBeforeMutation_WhenHandlingUpdateScopePermission_ThenNoFieldChanges()
    {
        // Given a non-owner with a request that would otherwise change the row: AF-33e runs before
        // mutation so the refusing handler leaves every field untouched
        var scopes = new AsyncFakeRepository<Scope>();
        var permissions = new AsyncFakeRepository<ScopePermission>();
        var scope = await SeedScopeAsync(scopes);
        var permission = await SeedPermissionAsync(permissions, scope);
        var handler = Handler(permissions, OwnershipChecker(allowed: false));

        // When
        var output = await handler.HandleAsync(
            Command(scope.PublicId, permission.PublicId, (int)Roles.ScopeAdmin, Guid.NewGuid()));

        // Then
        Assert.False(output.Success);
        var stored = await StoredAsync(permissions, permission.PublicId);
        Assert.Equal("billing:read", stored.Name);
        Assert.Equal("Read billing records.", stored.Description);
        Assert.True(stored.IncludeAsJwtClaim);
    }
}