using ArturRios.Heimdall.Command.Handlers;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Heimdall.Shared.Services;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Mock;
using Moq;

namespace ArturRios.Heimdall.Command.Tests;

// Unit tests for DeleteScopePermissionCommandHandler (UC-34): the main flow for a System Admin and
// for an owning Scope Admin, AF-34a (permission missing, in another scope, or addressed through an
// unknown scope), AF-34b (already deleted — an idempotent success that writes nothing), and AF-34e
// (an actor who does not own the scope), including AF-34e taking priority over AF-34b for a
// non-owner. A `User` never reaches the handler — [RoleRequirement] refuses them at the endpoint,
// covered in ScopePermissionControllerDeleteTests.
public class DeleteScopePermissionCommandHandlerTests
{
    // A fixed, obviously-not-now timestamp, so "UpdatedAt was (not) stamped" is a meaningful assertion.
    private static readonly DateTime Stamp = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

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
            Scope = scope,
            UpdatedAt = Stamp
        };
        await permissions.CreateAsync(permission);
        return permission;
    }

    private static DeleteScopePermissionCommand Command(
        Guid scopeId, Guid id, int actingRole, Guid actingPersonId) => new()
    {
        ScopeId = scopeId,
        Id = id,
        ActingRole = actingRole,
        ActingPersonId = actingPersonId
    };

    private static DeleteScopePermissionCommandHandler Handler(
        AsyncFakeRepository<ScopePermission> permissions, IScopeOwnershipChecker? ownership = null) =>
        new(permissions, permissions, ownership ?? OwnershipChecker());

    private static async Task<ScopePermission> StoredAsync(
        AsyncFakeRepository<ScopePermission> permissions, Guid publicId) =>
        (await permissions.GetAllAsync()).Data!.Single(p => p.PublicId == publicId);

    [UnitFact]
    public async Task GivenSystemAdmin_WhenHandlingDeleteScopePermission_ThenPermissionIsLogicallyDeleted()
    {
        // Given an active permission (UC-34 main flow)
        var scopes = new AsyncFakeRepository<Scope>();
        var permissions = new AsyncFakeRepository<ScopePermission>();
        var scope = await SeedScopeAsync(scopes);
        var permission = await SeedPermissionAsync(permissions, scope);
        var handler = Handler(permissions);

        // When
        var output = await handler.HandleAsync(
            Command(scope.PublicId, permission.PublicId, (int)Roles.SystemAdmin, Guid.NewGuid()));

        // Then
        Assert.True(output.Success);
        Assert.Equal(permission.PublicId, output.Data!.Id);
        Assert.False(output.Data.AlreadyDeleted);
        Assert.Contains(ScopePermissionMessages.ScopePermissionDeletedSuccessfully, output.Messages);
        Assert.True((await StoredAsync(permissions, permission.PublicId)).IsDeleted);
    }

    [UnitFact]
    public async Task GivenOwningScopeAdmin_WhenHandlingDeleteScopePermission_ThenPermissionIsLogicallyDeleted()
    {
        // Given a ScopeAdmin who owns the scope deleting the permission (UC-34 step 2)
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
        Assert.False(output.Data!.AlreadyDeleted);
        Assert.True((await StoredAsync(permissions, permission.PublicId)).IsDeleted);
    }

    [UnitFact]
    public async Task GivenActivePermission_WhenHandlingDeleteScopePermission_ThenUpdatedAtIsStampedAndCreatedAtIsNot()
    {
        // Given an existing permission (UC-34 step 3: no DB trigger maintains UpdatedAt)
        var scopes = new AsyncFakeRepository<Scope>();
        var permissions = new AsyncFakeRepository<ScopePermission>();
        var scope = await SeedScopeAsync(scopes);
        var permission = await SeedPermissionAsync(permissions, scope);
        var createdAt = permission.CreatedAt;
        var before = DateTime.UtcNow;
        var handler = Handler(permissions);

        // When
        var output = await handler.HandleAsync(
            Command(scope.PublicId, permission.PublicId, (int)Roles.SystemAdmin, Guid.NewGuid()));

        // Then
        Assert.True(output.Success);

        var stored = await StoredAsync(permissions, permission.PublicId);
        Assert.True(stored.UpdatedAt >= before);
        Assert.Equal(createdAt, stored.CreatedAt);
    }

    [UnitFact]
    public async Task GivenAlreadyDeletedPermission_WhenHandlingDeleteScopePermission_ThenSuccessReportsAlreadyDeleted()
    {
        // Given a permission that is already logically deleted (AF-34b: idempotent)
        var scopes = new AsyncFakeRepository<Scope>();
        var permissions = new AsyncFakeRepository<ScopePermission>();
        var scope = await SeedScopeAsync(scopes);
        var permission = await SeedPermissionAsync(permissions, scope, isDeleted: true);
        var handler = Handler(permissions);

        // When
        var output = await handler.HandleAsync(
            Command(scope.PublicId, permission.PublicId, (int)Roles.SystemAdmin, Guid.NewGuid()));

        // Then — the same success as the main flow, distinguished only by the flag
        Assert.True(output.Success);
        Assert.Equal(permission.PublicId, output.Data!.Id);
        Assert.True(output.Data.AlreadyDeleted);
        Assert.Contains(ScopePermissionMessages.ScopePermissionDeletedSuccessfully, output.Messages);
    }

    [UnitFact]
    public async Task GivenAlreadyDeletedPermission_WhenHandlingDeleteScopePermission_ThenNothingIsWritten()
    {
        // Given an already-deleted permission: the row carries the state the request asks for, so
        // re-stamping UpdatedAt would misreport when the deletion happened
        var scopes = new AsyncFakeRepository<Scope>();
        var permissions = new AsyncFakeRepository<ScopePermission>();
        var scope = await SeedScopeAsync(scopes);
        var permission = await SeedPermissionAsync(permissions, scope, isDeleted: true);
        var handler = Handler(permissions);

        // When
        await handler.HandleAsync(
            Command(scope.PublicId, permission.PublicId, (int)Roles.SystemAdmin, Guid.NewGuid()));

        // Then
        var stored = await StoredAsync(permissions, permission.PublicId);
        Assert.True(stored.IsDeleted);
        Assert.Equal(Stamp, stored.UpdatedAt);
    }

    [UnitFact]
    public async Task GivenUnknownPermission_WhenHandlingDeleteScopePermission_ThenNotFoundIsReported()
    {
        // Given a permission id nobody holds (AF-34a)
        var scopes = new AsyncFakeRepository<Scope>();
        var permissions = new AsyncFakeRepository<ScopePermission>();
        var scope = await SeedScopeAsync(scopes);
        var permission = await SeedPermissionAsync(permissions, scope);
        var handler = Handler(permissions);

        // When
        var output = await handler.HandleAsync(
            Command(scope.PublicId, Guid.NewGuid(), (int)Roles.SystemAdmin, Guid.NewGuid()));

        // Then
        Assert.False(output.Success);
        Assert.Contains(ScopePermissionMessages.ScopePermissionNotFound, output.Errors);
        Assert.False((await StoredAsync(permissions, permission.PublicId)).IsDeleted);
    }

    [UnitFact]
    public async Task GivenPermissionOfADifferentScope_WhenHandlingDeleteScopePermission_ThenNotFoundIsReported()
    {
        // Given a permission that exists, but under a different scope than the path addresses:
        // qualified by the route's scopeId (AF-34a)
        var scopes = new AsyncFakeRepository<Scope>();
        var permissions = new AsyncFakeRepository<ScopePermission>();
        var scope = await SeedScopeAsync(scopes);
        var otherScope = await SeedScopeAsync(scopes, "Other");
        var permission = await SeedPermissionAsync(permissions, otherScope);
        var handler = Handler(permissions);

        // When
        var output = await handler.HandleAsync(
            Command(scope.PublicId, permission.PublicId, (int)Roles.SystemAdmin, Guid.NewGuid()));

        // Then
        Assert.False(output.Success);
        Assert.Contains(ScopePermissionMessages.ScopePermissionNotFound, output.Errors);
        Assert.False((await StoredAsync(permissions, permission.PublicId)).IsDeleted);
    }

    [UnitFact]
    public async Task GivenUnknownScope_WhenHandlingDeleteScopePermission_ThenNotFoundIsReported()
    {
        // Given a scope id nobody holds: an unknown scope is the same one 404 (AF-34a)
        var scopes = new AsyncFakeRepository<Scope>();
        var permissions = new AsyncFakeRepository<ScopePermission>();
        var scope = await SeedScopeAsync(scopes);
        var permission = await SeedPermissionAsync(permissions, scope);
        var handler = Handler(permissions);

        // When
        var output = await handler.HandleAsync(
            Command(Guid.NewGuid(), permission.PublicId, (int)Roles.SystemAdmin, Guid.NewGuid()));

        // Then
        Assert.False(output.Success);
        Assert.Contains(ScopePermissionMessages.ScopePermissionNotFound, output.Errors);
        Assert.False((await StoredAsync(permissions, permission.PublicId)).IsDeleted);
    }

    [UnitFact]
    public async Task GivenNonOwningScopeAdmin_WhenHandlingDeleteScopePermission_ThenNotScopeOwnerIsReported()
    {
        // Given the ownership checker rejects the acting ScopeAdmin (AF-34e)
        var scopes = new AsyncFakeRepository<Scope>();
        var permissions = new AsyncFakeRepository<ScopePermission>();
        var scope = await SeedScopeAsync(scopes);
        var permission = await SeedPermissionAsync(permissions, scope);
        var handler = Handler(permissions, OwnershipChecker(allowed: false));

        // When
        var output = await handler.HandleAsync(
            Command(scope.PublicId, permission.PublicId, (int)Roles.ScopeAdmin, Guid.NewGuid()));

        // Then — refused, and the row is still active
        Assert.False(output.Success);
        Assert.Contains(ScopePermissionMessages.NotScopeOwner, output.Errors);

        var stored = await StoredAsync(permissions, permission.PublicId);
        Assert.False(stored.IsDeleted);
        Assert.Equal(Stamp, stored.UpdatedAt);
    }

    [UnitFact]
    public async Task GivenNonOwnerAndAlreadyDeletedPermission_WhenHandlingDeleteScopePermission_ThenNotScopeOwnerIsReported()
    {
        // Given a non-owner addressing an already-deleted permission: authorization runs before the
        // AF-34b no-op, so the idempotent success cannot be used to probe scopes the caller may not
        // act on
        var scopes = new AsyncFakeRepository<Scope>();
        var permissions = new AsyncFakeRepository<ScopePermission>();
        var scope = await SeedScopeAsync(scopes);
        var permission = await SeedPermissionAsync(permissions, scope, isDeleted: true);
        var handler = Handler(permissions, OwnershipChecker(allowed: false));

        // When
        var output = await handler.HandleAsync(
            Command(scope.PublicId, permission.PublicId, (int)Roles.ScopeAdmin, Guid.NewGuid()));

        // Then
        Assert.False(output.Success);
        Assert.Contains(ScopePermissionMessages.NotScopeOwner, output.Errors);
    }
}