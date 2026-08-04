using ArturRios.IdentityManager.Domain.Entities;
using ArturRios.IdentityManager.Domain.Enums;
using ArturRios.IdentityManager.Query.Handlers;
using ArturRios.IdentityManager.Query.Input;
using ArturRios.IdentityManager.Shared.Messages;
using ArturRios.IdentityManager.Shared.Services;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Mock;
using Moq;

namespace ArturRios.IdentityManager.Query.Tests;

// Unit tests for GetScopePermissionByIdQueryHandler (UC-32, FR-SP-04/FR-SP-09): a System Admin reads
// any scope permission, anyone else must own the scope. Covers the main flow for both, the output
// carrying only public identifiers plus the Description/IncludeAsJwtClaim fields, AF-32a (unknown id,
// wrong scope, unknown scope, logically deleted), AF-32e (a non-owning Scope Admin), and the
// include-deleted behavior. A scope permission has no owner of its own, so authorization is the
// scope-ownership check, consulted after the not-found gate so both alternative flows stay observable.
public class GetScopePermissionByIdQueryHandlerTests
{
    private static Scope Scope(long id) =>
        new() { Id = id, PublicId = Guid.NewGuid(), Name = $"scope-{id}" };

    private static ScopePermission Permission(
        long id, Scope scope, bool isDeleted = false, bool includeAsJwtClaim = false,
        string? name = null, string? description = null) => new()
    {
        Id = id,
        PublicId = Guid.NewGuid(),
        Name = name ?? $"permission-{id}",
        Description = description ?? $"desc-{id}",
        IncludeAsJwtClaim = includeAsJwtClaim,
        IsDeleted = isDeleted,
        ScopeId = scope.Id,
        Scope = scope,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    private static IScopeOwnershipChecker Ownership(bool allowed = true)
    {
        var checker = new Mock<IScopeOwnershipChecker>();
        checker
            .Setup(c => c.ActorMayManageScopeAsync(It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<long>()))
            .ReturnsAsync(allowed);
        return checker.Object;
    }

    private static async Task<AsyncFakeRepository<ScopePermission>> PermissionsWith(
        params ScopePermission[] permissions)
    {
        var repository = new AsyncFakeRepository<ScopePermission>();

        foreach (var permission in permissions)
        {
            await repository.CreateAsync(permission);
        }

        return repository;
    }

    private static GetScopePermissionByIdQuery QueryFor(
        Scope scope, ScopePermission permission, int actingRole, Guid actingPersonId,
        bool includeDeleted = false) => new()
    {
        ScopeId = scope.PublicId,
        Id = permission.PublicId,
        IncludeDeleted = includeDeleted,
        ActingRole = actingRole,
        ActingPersonId = actingPersonId
    };

    [UnitFact]
    public async Task GivenSystemAdmin_WhenHandlingGetScopePermissionById_ThenPermissionIsReturned()
    {
        // Given a permission a System Admin does not own the scope of (UC-32 main flow)
        var scope = Scope(1);
        var permission = Permission(100, scope);
        var handler = new GetScopePermissionByIdQueryHandler(
            await PermissionsWith(permission), Ownership(allowed: true));

        // When
        var output = await handler.HandleAsync(
            QueryFor(scope, permission, (int)Roles.SystemAdmin, Guid.NewGuid()));

        // Then
        Assert.True(output.Success);
        Assert.Equal(permission.PublicId, output.Data!.Id);
        Assert.Contains(ScopePermissionMessages.ScopePermissionRetrievedSuccessfully, output.Messages);
    }

    [UnitFact]
    public async Task GivenOwningScopeAdmin_WhenHandlingGetScopePermissionById_ThenPermissionIsReturned()
    {
        // Given the acting Scope Admin owns the scope the permission lives in (UC-32 main flow)
        var scope = Scope(1);
        var permission = Permission(100, scope);
        var caller = Guid.NewGuid();
        var handler = new GetScopePermissionByIdQueryHandler(
            await PermissionsWith(permission), Ownership(allowed: true));

        // When
        var output = await handler.HandleAsync(
            QueryFor(scope, permission, (int)Roles.ScopeAdmin, caller));

        // Then
        Assert.True(output.Success);
        Assert.Equal(permission.PublicId, output.Data!.Id);
    }

    [UnitFact]
    public async Task GivenReturnedPermission_WhenHandlingGetScopePermissionById_ThenOutputCarriesPublicIdentifiersAndFlag()
    {
        // Given a permission whose internal scope key differs from its public ids, and a flagged claim
        var scope = Scope(7);
        var permission = Permission(700, scope, includeAsJwtClaim: true, name: "billing:read", description: "Read billing.");
        var handler = new GetScopePermissionByIdQueryHandler(
            await PermissionsWith(permission), Ownership(allowed: true));

        // When
        var output = await handler.HandleAsync(
            QueryFor(scope, permission, (int)Roles.SystemAdmin, Guid.NewGuid()));

        // Then — the scope is identified by PublicId, never by the bigint foreign key, and the new
        // flag/description round-trip onto the output
        Assert.Equal(scope.PublicId, output.Data!.ScopeId);
        Assert.Equal(permission.Name, output.Data.Name);
        Assert.Equal(permission.Description, output.Data.Description);
        Assert.True(output.Data.IncludeAsJwtClaim);
        Assert.False(output.Data.IsDeleted);
    }

    [UnitFact]
    public async Task GivenNonOwningScopeAdmin_WhenHandlingGetScopePermissionById_ThenNotScopeOwnerIsReported()
    {
        // Given a Scope Admin the ownership checker rejects (AF-32e). Not-found runs first, so the
        // refusal here proves the permission was found — a non-owner cannot probe for ids silently.
        var scope = Scope(1);
        var permission = Permission(100, scope);
        var handler = new GetScopePermissionByIdQueryHandler(
            await PermissionsWith(permission), Ownership(allowed: false));

        // When
        var output = await handler.HandleAsync(
            QueryFor(scope, permission, (int)Roles.ScopeAdmin, Guid.NewGuid()));

        // Then
        Assert.False(output.Success);
        Assert.Contains(ScopePermissionMessages.NotScopeOwner, output.Errors);
    }

    [UnitFact]
    public async Task GivenUnknownPermission_WhenHandlingGetScopePermissionById_ThenScopePermissionNotFoundIsReported()
    {
        // Given an id nobody holds (AF-32a)
        var scope = Scope(1);
        var handler = new GetScopePermissionByIdQueryHandler(
            await PermissionsWith(), Ownership(allowed: true));

        // When
        var output = await handler.HandleAsync(new GetScopePermissionByIdQuery
        {
            ScopeId = scope.PublicId,
            Id = Guid.NewGuid(),
            ActingRole = (int)Roles.SystemAdmin,
            ActingPersonId = Guid.NewGuid()
        });

        // Then
        Assert.False(output.Success);
        Assert.Contains(ScopePermissionMessages.ScopePermissionNotFound, output.Errors);
    }

    [UnitFact]
    public async Task GivenPermissionOfADifferentScope_WhenHandlingGetScopePermissionById_ThenScopePermissionNotFoundIsReported()
    {
        // Given the permission exists, but under another scope than the one addressed (AF-32a)
        var scope = Scope(1);
        var otherScope = Scope(2);
        var permission = Permission(100, otherScope);
        var handler = new GetScopePermissionByIdQueryHandler(
            await PermissionsWith(permission), Ownership(allowed: true));

        // When
        var output = await handler.HandleAsync(
            QueryFor(scope, permission, (int)Roles.SystemAdmin, Guid.NewGuid()));

        // Then
        Assert.False(output.Success);
        Assert.Contains(ScopePermissionMessages.ScopePermissionNotFound, output.Errors);
    }

    [UnitFact]
    public async Task GivenUnknownScope_WhenHandlingGetScopePermissionById_ThenScopePermissionNotFoundIsReported()
    {
        // Given a scope id nobody holds: the route qualifies the lookup, so nothing matches (AF-32a)
        var scope = Scope(1);
        var permission = Permission(100, scope);
        var handler = new GetScopePermissionByIdQueryHandler(
            await PermissionsWith(permission), Ownership(allowed: true));

        // When
        var output = await handler.HandleAsync(new GetScopePermissionByIdQuery
        {
            ScopeId = Guid.NewGuid(),
            Id = permission.PublicId,
            ActingRole = (int)Roles.SystemAdmin,
            ActingPersonId = Guid.NewGuid()
        });

        // Then
        Assert.False(output.Success);
        Assert.Contains(ScopePermissionMessages.ScopePermissionNotFound, output.Errors);
    }

    [UnitFact]
    public async Task GivenDeletedPermissionAndIncludeDeletedFalse_WhenHandlingGetScopePermissionById_ThenScopePermissionNotFoundIsReported()
    {
        // Given a logically deleted permission and no explicit request for it (FR-SP-09, AF-32a)
        var scope = Scope(1);
        var permission = Permission(100, scope, isDeleted: true);
        var handler = new GetScopePermissionByIdQueryHandler(
            await PermissionsWith(permission), Ownership(allowed: true));

        // When
        var output = await handler.HandleAsync(
            QueryFor(scope, permission, (int)Roles.SystemAdmin, Guid.NewGuid()));

        // Then
        Assert.False(output.Success);
        Assert.Contains(ScopePermissionMessages.ScopePermissionNotFound, output.Errors);
    }

    [UnitFact]
    public async Task GivenDeletedPermissionAndIncludeDeletedTrue_WhenHandlingGetScopePermissionById_ThenPermissionIsReturned()
    {
        // Given a logically deleted permission explicitly requested (FR-SP-09)
        var scope = Scope(1);
        var permission = Permission(100, scope, isDeleted: true);
        var handler = new GetScopePermissionByIdQueryHandler(
            await PermissionsWith(permission), Ownership(allowed: true));

        // When
        var output = await handler.HandleAsync(
            QueryFor(scope, permission, (int)Roles.SystemAdmin, Guid.NewGuid(), includeDeleted: true));

        // Then
        Assert.True(output.Success);
        Assert.True(output.Data!.IsDeleted);
    }
}
