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

// Unit tests for ListScopePermissionsQueryHandler (UC-32, FR-SP-05/FR-SP-09): a System Admin sees
// every permission in the scope, and — unlike applications — so does an owning Scope Admin, because a
// scope permission has no owner of its own. Covers the main flow for both, a missing or logically
// deleted scope (AF-31a reused), a non-owning actor (AF-32e), the include-deleted behavior, the name
// filter, pagination, and the Description/IncludeAsJwtClaim projection.
public class ListScopePermissionsQueryHandlerTests
{
    private static Scope Scope(long id, bool isDeleted = false) =>
        new() { Id = id, PublicId = Guid.NewGuid(), Name = $"scope-{id}", IsDeleted = isDeleted };

    private static ScopePermission Permission(
        long id, Scope scope, string? name = null, bool isDeleted = false,
        bool includeAsJwtClaim = false, string? description = null) => new()
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

    private static IScopeOwnershipChecker Ownership(bool allowed)
    {
        var checker = new Mock<IScopeOwnershipChecker>();
        checker
            .Setup(x => x.ActorMayManageScopeAsync(It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<long>()))
            .ReturnsAsync(allowed);
        return checker.Object;
    }

    private static async Task<AsyncFakeRepository<Scope>> ScopesWith(params Scope[] scopes)
    {
        var repository = new AsyncFakeRepository<Scope>();

        foreach (var scope in scopes)
        {
            await repository.CreateAsync(scope);
        }

        return repository;
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

    private static ListScopePermissionsQuery QueryFor(
        Scope scope, int actingRole, Guid actingPersonId, int pageSize = 10) => new()
    {
        ScopeId = scope.PublicId,
        PageNumber = 1,
        PageSize = pageSize,
        ActingRole = actingRole,
        ActingPersonId = actingPersonId
    };

    [UnitFact]
    public async Task GivenSystemAdmin_WhenHandlingListScopePermissions_ThenEveryPermissionInTheScopeIsReturned()
    {
        // Given a scope with two permissions (UC-32 main flow)
        var scope = Scope(1);
        var first = Permission(100, scope, "Alpha");
        var second = Permission(101, scope, "Beta");
        var handler = new ListScopePermissionsQueryHandler(
            await ScopesWith(scope), await PermissionsWith(first, second), Ownership(allowed: true));

        // When
        var output = await handler.HandleAsync(QueryFor(scope, (int)Roles.SystemAdmin, Guid.NewGuid()));

        // Then
        Assert.True(output.Success);
        Assert.Equal(2, output.TotalItems);
        Assert.Equal([first.PublicId, second.PublicId], output.Data!.Select(x => x.Id));
        Assert.Contains(ScopePermissionMessages.ScopePermissionsRetrievedSuccessfully, output.Messages);
    }

    [UnitFact]
    public async Task GivenOwningScopeAdmin_WhenHandlingListScopePermissions_ThenAllPermissionsInTheScopeAreReturned()
    {
        // Given an owning Scope Admin: a scope permission has no owner of its own, so there is no
        // per-owner narrowing — owning the scope is the whole of the rule, and every permission in it
        // is visible (contrast UC-17, where a Scope Admin sees only their own applications)
        var scope = Scope(1);
        var first = Permission(100, scope, "Alpha");
        var second = Permission(101, scope, "Beta");
        var handler = new ListScopePermissionsQueryHandler(
            await ScopesWith(scope), await PermissionsWith(first, second), Ownership(allowed: true));

        // When
        var output = await handler.HandleAsync(QueryFor(scope, (int)Roles.ScopeAdmin, Guid.NewGuid()));

        // Then
        Assert.True(output.Success);
        Assert.Equal(2, output.TotalItems);
        Assert.Equal([first.PublicId, second.PublicId], output.Data!.Select(x => x.Id));
    }

    [UnitFact]
    public async Task GivenPermissionsOfAnotherScope_WhenHandlingListScopePermissions_ThenTheyAreNotReturned()
    {
        // Given a permission in a different scope entirely
        var scope = Scope(1);
        var otherScope = Scope(2);
        var inside = Permission(100, scope, "Alpha");
        var outside = Permission(200, otherScope, "Beta");
        var handler = new ListScopePermissionsQueryHandler(
            await ScopesWith(scope, otherScope), await PermissionsWith(inside, outside),
            Ownership(allowed: true));

        // When
        var output = await handler.HandleAsync(QueryFor(scope, (int)Roles.SystemAdmin, Guid.NewGuid()));

        // Then
        Assert.Equal(1, output.TotalItems);
        Assert.Equal(inside.PublicId, output.Data!.Single().Id);
    }

    [UnitFact]
    public async Task GivenMissingScope_WhenHandlingListScopePermissions_ThenScopeNotFoundIsReported()
    {
        // Given an empty scope store (AF-31a reused)
        var scope = Scope(1);
        var handler = new ListScopePermissionsQueryHandler(
            await ScopesWith(), await PermissionsWith(), Ownership(allowed: true));

        // When
        var output = await handler.HandleAsync(QueryFor(scope, (int)Roles.SystemAdmin, Guid.NewGuid()));

        // Then
        Assert.False(output.Success);
        Assert.Contains(ScopePermissionMessages.ScopeNotFound, output.Errors);
    }

    [UnitFact]
    public async Task GivenLogicallyDeletedScope_WhenHandlingListScopePermissions_ThenScopeNotFoundIsReported()
    {
        // Given a logically deleted scope (AF-31a reused)
        var scope = Scope(1, isDeleted: true);
        var handler = new ListScopePermissionsQueryHandler(
            await ScopesWith(scope), await PermissionsWith(), Ownership(allowed: true));

        // When
        var output = await handler.HandleAsync(QueryFor(scope, (int)Roles.SystemAdmin, Guid.NewGuid()));

        // Then
        Assert.False(output.Success);
        Assert.Contains(ScopePermissionMessages.ScopeNotFound, output.Errors);
    }

    [UnitFact]
    public async Task GivenScopeAdminWhoDoesNotOwnTheScope_WhenHandlingListScopePermissions_ThenNotScopeOwnerIsReported()
    {
        // Given the ownership checker rejects the actor (AF-32e). The refusal is distinct from an
        // empty page: it says the caller has no standing in this scope at all.
        var scope = Scope(1);
        var permission = Permission(100, scope, "Alpha");
        var handler = new ListScopePermissionsQueryHandler(
            await ScopesWith(scope), await PermissionsWith(permission), Ownership(allowed: false));

        // When
        var output = await handler.HandleAsync(QueryFor(scope, (int)Roles.ScopeAdmin, Guid.NewGuid()));

        // Then
        Assert.False(output.Success);
        Assert.Contains(ScopePermissionMessages.NotScopeOwner, output.Errors);
    }

    [UnitFact]
    public async Task GivenDeletedPermissions_WhenHandlingListScopePermissions_ThenTheyAreExcludedByDefault()
    {
        // Given one active and one logically deleted permission (FR-SP-09)
        var scope = Scope(1);
        var active = Permission(100, scope, "Alpha");
        var deleted = Permission(101, scope, "Beta", isDeleted: true);
        var handler = new ListScopePermissionsQueryHandler(
            await ScopesWith(scope), await PermissionsWith(active, deleted), Ownership(allowed: true));

        // When
        var output = await handler.HandleAsync(QueryFor(scope, (int)Roles.SystemAdmin, Guid.NewGuid()));

        // Then
        Assert.Equal(1, output.TotalItems);
        Assert.Equal(active.PublicId, output.Data!.Single().Id);
    }

    [UnitFact]
    public async Task GivenIncludeDeleted_WhenHandlingListScopePermissions_ThenDeletedPermissionsAreReturned()
    {
        // Given deleted permissions explicitly requested (FR-SP-09)
        var scope = Scope(1);
        var active = Permission(100, scope, "Alpha");
        var deleted = Permission(101, scope, "Beta", isDeleted: true);
        var handler = new ListScopePermissionsQueryHandler(
            await ScopesWith(scope), await PermissionsWith(active, deleted), Ownership(allowed: true));

        var query = QueryFor(scope, (int)Roles.SystemAdmin, Guid.NewGuid());
        query.IncludeDeleted = true;

        // When
        var output = await handler.HandleAsync(query);

        // Then
        Assert.Equal(2, output.TotalItems);
    }

    [UnitFact]
    public async Task GivenNameFilter_WhenHandlingListScopePermissions_ThenOnlyMatchingPermissionsAreReturned()
    {
        // Given a case-insensitive substring filter
        var scope = Scope(1);
        var billing = Permission(100, scope, "billing:read");
        var reporting = Permission(101, scope, "reporting:read");
        var handler = new ListScopePermissionsQueryHandler(
            await ScopesWith(scope), await PermissionsWith(billing, reporting), Ownership(allowed: true));

        var query = QueryFor(scope, (int)Roles.SystemAdmin, Guid.NewGuid());
        query.Name = "BILLING";

        // When
        var output = await handler.HandleAsync(query);

        // Then
        Assert.Equal(1, output.TotalItems);
        Assert.Equal(billing.PublicId, output.Data!.Single().Id);
    }

    [UnitFact]
    public async Task GivenPageSize_WhenHandlingListScopePermissions_ThenResultsArePagedByName()
    {
        // Given three permissions and a page size of two, ordered by name
        var scope = Scope(1);
        var charlie = Permission(100, scope, "Charlie");
        var alpha = Permission(101, scope, "Alpha");
        var bravo = Permission(102, scope, "Bravo");
        var handler = new ListScopePermissionsQueryHandler(
            await ScopesWith(scope), await PermissionsWith(charlie, alpha, bravo), Ownership(allowed: true));

        // When
        var output = await handler.HandleAsync(
            QueryFor(scope, (int)Roles.SystemAdmin, Guid.NewGuid(), pageSize: 2));

        // Then — the first page holds the two alphabetically first
        Assert.Equal(3, output.TotalItems);
        Assert.Equal([alpha.PublicId, bravo.PublicId], output.Data!.Select(x => x.Id));
    }

    [UnitFact]
    public async Task GivenPermissions_WhenHandlingListScopePermissions_ThenOutputCarriesDescriptionAndIncludeAsJwtClaim()
    {
        // Given a permission carrying a description and a flagged claim
        var scope = Scope(1);
        var permission = Permission(100, scope, "billing:read", includeAsJwtClaim: true, description: "Read billing records.");
        var handler = new ListScopePermissionsQueryHandler(
            await ScopesWith(scope), await PermissionsWith(permission), Ownership(allowed: true));

        // When
        var output = await handler.HandleAsync(QueryFor(scope, (int)Roles.SystemAdmin, Guid.NewGuid()));

        // Then — the new fields and the public scope id are projected onto the output
        var item = output.Data!.Single();
        Assert.Equal(permission.Description, item.Description);
        Assert.True(item.IncludeAsJwtClaim);
        Assert.Equal(scope.PublicId, item.ScopeId);
        Assert.Equal(permission.Name, item.Name);
    }
}
