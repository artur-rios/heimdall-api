using ArturRios.IdentityManager.Command.Handlers;
using ArturRios.IdentityManager.Command.Input;
using ArturRios.IdentityManager.Domain.Entities;
using ArturRios.IdentityManager.Domain.Enums;
using ArturRios.IdentityManager.Shared.Messages;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Mock;

namespace ArturRios.IdentityManager.Command.Tests;

// Unit tests for HardDeleteScopePermissionCommandHandler (UC-35): the main flow removing the record
// for good, the same treatment for an already logically deleted permission, and AF-35a (permission
// missing, in another scope, addressed through an unknown scope, or already hard deleted by an
// earlier call). Authorization is entirely the endpoint's: UC-35's only actor is the System Admin,
// the command carries no acting person, and the 403/401 flows are covered in
// ScopePermissionControllerHardDeleteTests.
public class HardDeleteScopePermissionCommandHandlerTests
{
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

    private static HardDeleteScopePermissionCommand Command(Guid scopeId, Guid id) => new()
    {
        ScopeId = scopeId,
        Id = id
    };

    private static HardDeleteScopePermissionCommandHandler Handler(
        AsyncFakeRepository<ScopePermission> permissions) =>
        new(permissions, permissions);

    private static async Task<IEnumerable<ScopePermission>> StoredAsync(
        AsyncFakeRepository<ScopePermission> permissions) =>
        (await permissions.GetAllAsync()).Data!;

    [UnitFact]
    public async Task GivenSystemAdmin_WhenHandlingHardDeleteScopePermission_ThenPermissionIsRemoved()
    {
        // Given an active permission (UC-35 main flow)
        var scopes = new AsyncFakeRepository<Scope>();
        var permissions = new AsyncFakeRepository<ScopePermission>();
        var scope = await SeedScopeAsync(scopes);
        var permission = await SeedPermissionAsync(permissions, scope);

        // When
        var output = await Handler(permissions).HandleAsync(Command(scope.PublicId, permission.PublicId));

        // Then — the response reports the permission, and the record is gone for good
        Assert.True(output.Success);
        Assert.Equal(permission.PublicId, output.Data!.Id);
        Assert.Contains(ScopePermissionMessages.ScopePermissionHardDeletedSuccessfully, output.Messages);
        Assert.Empty(await StoredAsync(permissions));
    }

    [UnitFact]
    public async Task GivenLogicallyDeletedPermission_WhenHandlingHardDeleteScopePermission_ThenPermissionIsRemoved()
    {
        // Given a permission already carrying IsDeleted — exactly what a cleanup pass starts from, so
        // the lookup omits the !IsDeleted filter
        var scopes = new AsyncFakeRepository<Scope>();
        var permissions = new AsyncFakeRepository<ScopePermission>();
        var scope = await SeedScopeAsync(scopes);
        var permission = await SeedPermissionAsync(permissions, scope, isDeleted: true);

        // When
        var output = await Handler(permissions).HandleAsync(Command(scope.PublicId, permission.PublicId));

        // Then
        Assert.True(output.Success);
        Assert.Equal(permission.PublicId, output.Data!.Id);
        Assert.Empty(await StoredAsync(permissions));
    }

    [UnitFact]
    public async Task GivenOutput_WhenInspectingIdentifiers_ThenItCarriesOnlyThePermissionPublicId()
    {
        // Given internal ids that must never leave the data layer (SRD §4.0)
        var scopes = new AsyncFakeRepository<Scope>();
        var permissions = new AsyncFakeRepository<ScopePermission>();
        var scope = await SeedScopeAsync(scopes);
        var permission = await SeedPermissionAsync(permissions, scope);

        // When
        var output = await Handler(permissions).HandleAsync(Command(scope.PublicId, permission.PublicId));

        // Then — the only identifier on the output is the permission's PublicId
        Assert.Equal(permission.PublicId, output.Data!.Id);
        Assert.NotEqual(Guid.Empty, output.Data.Id);
    }

    [UnitFact]
    public async Task
        GivenSiblingPermissionInTheSameScope_WhenHandlingHardDeleteScopePermission_ThenOnlyTheAddressedOneIsRemoved()
    {
        // Given two permissions of the same scope, only one of them addressed
        var scopes = new AsyncFakeRepository<Scope>();
        var permissions = new AsyncFakeRepository<ScopePermission>();
        var scope = await SeedScopeAsync(scopes);
        var target = await SeedPermissionAsync(permissions, scope);
        var sibling = await SeedPermissionAsync(permissions, scope);

        // When
        var output = await Handler(permissions).HandleAsync(Command(scope.PublicId, target.PublicId));

        // Then — the sibling survives untouched
        Assert.True(output.Success);
        var stored = (await StoredAsync(permissions)).ToList();
        Assert.Single(stored);
        Assert.Equal(sibling.PublicId, stored[0].PublicId);
    }

    [UnitFact]
    public async Task GivenUnknownPermission_WhenHandlingHardDeleteScopePermission_ThenNotFoundIsReported()
    {
        // Given a permission id nobody holds (AF-35a)
        var scopes = new AsyncFakeRepository<Scope>();
        var permissions = new AsyncFakeRepository<ScopePermission>();
        var scope = await SeedScopeAsync(scopes);
        await SeedPermissionAsync(permissions, scope);

        // When
        var output = await Handler(permissions).HandleAsync(Command(scope.PublicId, Guid.NewGuid()));

        // Then — refused, and the existing permission is untouched
        Assert.False(output.Success);
        Assert.Contains(ScopePermissionMessages.ScopePermissionNotFound, output.Errors);
        Assert.Single(await StoredAsync(permissions));
    }

    [UnitFact]
    public async Task GivenPermissionOfADifferentScope_WhenHandlingHardDeleteScopePermission_ThenNotFoundIsReported()
    {
        // Given the permission exists, but under a different scope than the command addresses (AF-35a)
        var scopes = new AsyncFakeRepository<Scope>();
        var permissions = new AsyncFakeRepository<ScopePermission>();
        var scope = await SeedScopeAsync(scopes);
        var otherScope = await SeedScopeAsync(scopes, "Other");
        var permission = await SeedPermissionAsync(permissions, otherScope);

        // When
        var output = await Handler(permissions).HandleAsync(Command(scope.PublicId, permission.PublicId));

        // Then — refused, and the row survives
        Assert.False(output.Success);
        Assert.Contains(ScopePermissionMessages.ScopePermissionNotFound, output.Errors);
        Assert.Single(await StoredAsync(permissions));
    }

    [UnitFact]
    public async Task GivenUnknownScope_WhenHandlingHardDeleteScopePermission_ThenNotFoundIsReported()
    {
        // Given a scope id nobody holds — an unknown scope and an unknown permission are one 404 (AF-35a)
        var scopes = new AsyncFakeRepository<Scope>();
        var permissions = new AsyncFakeRepository<ScopePermission>();
        var scope = await SeedScopeAsync(scopes);
        var permission = await SeedPermissionAsync(permissions, scope);

        // When
        var output = await Handler(permissions).HandleAsync(Command(Guid.NewGuid(), permission.PublicId));

        // Then
        Assert.False(output.Success);
        Assert.Contains(ScopePermissionMessages.ScopePermissionNotFound, output.Errors);
        Assert.Single(await StoredAsync(permissions));
    }

    [UnitFact]
    public async Task GivenAlreadyHardDeletedPermission_WhenHandlingHardDeleteScopePermission_ThenNotFoundIsReported()
    {
        // Given the same permission hard deleted twice: the row is gone, so the second call has
        // nothing to find. UC-35 has no idempotent path — unlike UC-34's AF-34b
        var scopes = new AsyncFakeRepository<Scope>();
        var permissions = new AsyncFakeRepository<ScopePermission>();
        var scope = await SeedScopeAsync(scopes);
        var permission = await SeedPermissionAsync(permissions, scope);
        var handler = Handler(permissions);

        // When
        var first = await handler.HandleAsync(Command(scope.PublicId, permission.PublicId));
        var second = await handler.HandleAsync(Command(scope.PublicId, permission.PublicId));

        // Then
        Assert.True(first.Success);
        Assert.False(second.Success);
        Assert.Contains(ScopePermissionMessages.ScopePermissionNotFound, second.Errors);
    }
}