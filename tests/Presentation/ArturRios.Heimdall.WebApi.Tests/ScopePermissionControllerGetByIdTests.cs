using System.Net;
using ArturRios.Configuration.Enums;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Query.Output;
using ArturRios.Heimdall.WebApi.Tests.Support;
using ArturRios.Output;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Functional;

namespace ArturRios.Heimdall.WebApi.Tests;

// Functional tests for GET /api/scopes/{scopeId}/permissions/{id} (UC-32, FR-SP-04/09): the main flow
// for a System Admin and for the owning Scope Admin, AF-32a (unknown id, wrong scope, logically
// deleted), AF-32e (a Scope Admin who does not own the scope), and the framework-level flows (403 for
// a User, 401 unauthenticated). A scope permission has no owner of its own, so owning the scope is
// the whole of the visibility rule.
[Collection(nameof(FunctionalCollection))]
public class ScopePermissionControllerGetByIdTests(PostgresFixture db) : WebApiTest<Program>(EnvironmentType.Local)
{
    private static string Route(Guid scopeId, Guid id) => $"/api/scopes/{scopeId}/permissions/{id}";

    private async Task<ScopePermission> SeedScopePermissionAsync(
        Scope scope, string? name = null, bool isDeleted = false, bool includeAsJwtClaim = false)
    {
        await using var context = db.CreateContext();
        var permission = new ScopePermission
        {
            PublicId = Guid.NewGuid(),
            Name = name ?? $"perm-{Guid.NewGuid():N}",
            Description = "A test permission.",
            IncludeAsJwtClaim = includeAsJwtClaim,
            IsDeleted = isDeleted,
            ScopeId = scope.Id
        };
        context.ScopePermissions.Add(permission);
        await context.SaveChangesAsync();
        return permission;
    }

    [FunctionalFact]
    public async Task GivenSystemAdmin_WhenGetScopePermissionById_ThenOkWithPermission()
    {
        // Given a scope permission a System Admin does not own (they own no scope)
        var scope = await Seeds.ScopeAsync(db);
        var permission = await SeedScopePermissionAsync(scope, "documents.read", includeAsJwtClaim: true);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.GetAsync<DataOutput<ScopePermissionOutput?>>(
            Route(scope.PublicId, permission.PublicId));

        // Then — the payload identifies the scope by PublicId, never by internal id, and carries the flag
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(permission.PublicId, response.Body?.Data?.Id);
        Assert.Equal(permission.Name, response.Body?.Data?.Name);
        Assert.Equal(permission.Description, response.Body?.Data?.Description);
        Assert.True(response.Body?.Data?.IncludeAsJwtClaim);
        Assert.Equal(scope.PublicId, response.Body?.Data?.ScopeId);
        Assert.False(response.Body?.Data?.IsDeleted);
    }

    [FunctionalFact]
    public async Task GivenOwningScopeAdmin_WhenGetScopePermissionById_ThenOk()
    {
        // Given the acting Scope Admin owns the scope the permission belongs to
        var scope = await Seeds.ScopeAsync(db);
        var admin = await Seeds.PersonAsync(db, Roles.ScopeAdmin, new PersonOptions { EmailPrefix = "admin", Owns = [scope] });
        var permission = await SeedScopePermissionAsync(scope);
        Authorize(TestTokens.For(admin.PublicId, (int)Roles.ScopeAdmin));

        // When — a Scope Admin who owns the scope sees any of its permissions
        var response = await Gateway.GetAsync<DataOutput<ScopePermissionOutput?>>(
            Route(scope.PublicId, permission.PublicId));

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(permission.PublicId, response.Body?.Data?.Id);
    }

    [FunctionalFact]
    public async Task GivenScopeAdminNotOwner_WhenGetScopePermissionById_ThenForbidden()
    {
        // Given a Scope Admin who does NOT own the scope (AF-32e)
        var scope = await Seeds.ScopeAsync(db);
        await Seeds.PersonAsync(db, Roles.ScopeAdmin, new PersonOptions { EmailPrefix = "admin", Owns = [scope] });
        var permission = await SeedScopePermissionAsync(scope);
        var stranger = await Seeds.PersonAsync(db, Roles.ScopeAdmin, new PersonOptions { EmailPrefix = "admin" });
        Authorize(TestTokens.For(stranger.PublicId, (int)Roles.ScopeAdmin));

        // When
        var response = await Gateway.GetAsync<DataOutput<ScopePermissionOutput?>>(
            Route(scope.PublicId, permission.PublicId));

        // Then
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenUserRole_WhenGetScopePermissionById_ThenForbidden()
    {
        // Given a caller holding the User role: the endpoint's [RoleRequirement] refuses them
        var scope = await Seeds.ScopeAsync(db);
        var permission = await SeedScopePermissionAsync(scope);
        var caller = await Seeds.PersonAsync(db, Roles.User, new PersonOptions { EmailPrefix = "user", MemberOf = scope });
        Authorize(TestTokens.For(caller.PublicId, (int)Roles.User));

        // When
        var response = await Gateway.GetAsync<DataOutput<ScopePermissionOutput?>>(
            Route(scope.PublicId, permission.PublicId));

        // Then
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenUnknownScopePermission_WhenGetScopePermissionById_ThenNotFound()
    {
        // Given a permission id nobody holds (AF-32a)
        var scope = await Seeds.ScopeAsync(db);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.GetAsync<DataOutput<ScopePermissionOutput?>>(
            Route(scope.PublicId, Guid.NewGuid()));

        // Then
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenScopePermissionOfAnotherScope_WhenGetScopePermissionById_ThenNotFound()
    {
        // Given the permission exists, but under a different scope than the path addresses (AF-32a)
        var scope = await Seeds.ScopeAsync(db);
        var otherScope = await Seeds.ScopeAsync(db);
        var permission = await SeedScopePermissionAsync(otherScope);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.GetAsync<DataOutput<ScopePermissionOutput?>>(
            Route(scope.PublicId, permission.PublicId));

        // Then
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenDeletedScopePermission_WhenGetScopePermissionById_ThenNotFound()
    {
        // Given a logically deleted permission and no explicit request for it (FR-SP-09, AF-32a)
        var scope = await Seeds.ScopeAsync(db);
        var permission = await SeedScopePermissionAsync(scope, isDeleted: true);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.GetAsync<DataOutput<ScopePermissionOutput?>>(
            Route(scope.PublicId, permission.PublicId));

        // Then
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenDeletedScopePermissionAndIncludeDeleted_WhenGetScopePermissionById_ThenOk()
    {
        // Given a logically deleted permission explicitly requested (FR-SP-09)
        var scope = await Seeds.ScopeAsync(db);
        var permission = await SeedScopePermissionAsync(scope, isDeleted: true);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.GetAsync<DataOutput<ScopePermissionOutput?>>(
            $"{Route(scope.PublicId, permission.PublicId)}?includeDeleted=true");

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Body?.Data?.IsDeleted);
    }

    [FunctionalFact]
    public async Task GivenNoToken_WhenGetScopePermissionById_ThenUnauthorized()
    {
        // Given no bearer token (precondition: the actor is authenticated)
        var scope = await Seeds.ScopeAsync(db);

        // When
        var response = await Gateway.GetAsync<DataOutput<ScopePermissionOutput?>>(
            Route(scope.PublicId, Guid.NewGuid()));

        // Then
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
