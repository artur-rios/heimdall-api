using System.Net;
using ArturRios.Configuration.Enums;
using ArturRios.IdentityManager.Domain.Entities;
using ArturRios.IdentityManager.Domain.Enums;
using ArturRios.IdentityManager.Query.Output;
using ArturRios.IdentityManager.WebApi.Tests.Support;
using ArturRios.Output;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Functional;

namespace ArturRios.IdentityManager.WebApi.Tests;

// Functional tests for GET /api/scopes/{scopeId}/permissions (UC-32, FR-SP-05/09): a System Admin sees
// every permission in the scope, and so does an owning Scope Admin — a scope permission has no owner
// of its own, so there is no per-owner narrowing. Covers AF-31a (unknown or logically deleted scope,
// reused by the listing), AF-32e (a Scope Admin who does not own the scope, and a User at the
// framework layer), the name filter and pagination, include-deleted, the 401, and that a forged acting
// role in the query string is discarded.
[Collection(nameof(FunctionalCollection))]
public class ScopePermissionControllerListTests(PostgresFixture db) : WebApiTest<Program>(EnvironmentType.Local)
{
    private static string Route(Guid scopeId) => $"/api/scopes/{scopeId}/permissions?pageNumber=1&pageSize=10";

    private async Task<Scope> SeedScopeAsync(bool isDeleted = false)
    {
        await using var context = db.CreateContext();
        var scope = new Scope
        {
            PublicId = Guid.NewGuid(), Name = $"scope-{Guid.NewGuid():N}", IsDeleted = isDeleted
        };
        context.Scopes.Add(scope);
        await context.SaveChangesAsync();
        return scope;
    }

    private async Task<Person> SeedScopeAdminAsync(Scope? ownedScope = null)
    {
        await using var context = db.CreateContext();
        var person = new Person
        {
            PublicId = Guid.NewGuid(), Name = "Admin",
            Email = $"admin-{Guid.NewGuid():N}@test.local",
            RoleId = (long)Roles.ScopeAdmin, EmailVerified = true
        };
        context.Persons.Add(person);
        await context.SaveChangesAsync();

        if (ownedScope is not null)
        {
            context.ScopeOwners.Add(new ScopeOwner { ScopeId = ownedScope.Id, PersonId = person.Id });
            await context.SaveChangesAsync();
        }

        return person;
    }

    private async Task<Person> SeedUserAsync(Scope scope)
    {
        await using var context = db.CreateContext();
        var person = new Person
        {
            PublicId = Guid.NewGuid(), Name = "User",
            Email = $"user-{Guid.NewGuid():N}@test.local",
            RoleId = (long)Roles.User, EmailVerified = true
        };
        context.Persons.Add(person);
        await context.SaveChangesAsync();

        context.ScopeUsers.Add(new ScopeUser { ScopeId = scope.Id, PersonId = person.Id });
        await context.SaveChangesAsync();

        return person;
    }

    private async Task<ScopePermission> SeedScopePermissionAsync(Scope scope, string name, bool isDeleted = false)
    {
        await using var context = db.CreateContext();
        var permission = new ScopePermission
        {
            PublicId = Guid.NewGuid(),
            Name = name,
            Description = "A test permission.",
            IncludeAsJwtClaim = false,
            IsDeleted = isDeleted,
            ScopeId = scope.Id
        };
        context.ScopePermissions.Add(permission);
        await context.SaveChangesAsync();
        return permission;
    }

    [FunctionalFact]
    public async Task GivenSystemAdmin_WhenGetScopePermissions_ThenOkWithEveryPermissionInTheScope()
    {
        // Given a scope with two permissions, plus one in another scope
        var scope = await SeedScopeAsync();
        var otherScope = await SeedScopeAsync();
        var alpha = await SeedScopePermissionAsync(scope, "Alpha");
        var beta = await SeedScopePermissionAsync(scope, "Beta");
        await SeedScopePermissionAsync(otherScope, "Gamma");
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.GetAsync<PaginatedOutput<ScopePermissionOutput>>(Route(scope.PublicId));

        // Then — both of the scope's permissions, and nothing from the other scope
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, response.Body?.TotalItems);
        Assert.Equal([alpha.PublicId, beta.PublicId], response.Body!.Data!.Select(x => x.Id));
    }

    [FunctionalFact]
    public async Task GivenOwningScopeAdmin_WhenGetScopePermissions_ThenOkWithEveryPermissionInTheScope()
    {
        // Given a ScopeAdmin who owns the scope: a scope permission has no owner of its own, so owning
        // the scope is the whole of the rule — they see every permission in it
        var scope = await SeedScopeAsync();
        var admin = await SeedScopeAdminAsync(ownedScope: scope);
        var alpha = await SeedScopePermissionAsync(scope, "Alpha");
        var beta = await SeedScopePermissionAsync(scope, "Beta");
        Authorize(TestTokens.For(admin.PublicId, (int)Roles.ScopeAdmin));

        // When
        var response = await Gateway.GetAsync<PaginatedOutput<ScopePermissionOutput>>(Route(scope.PublicId));

        // Then — both, not narrowed to any "own" subset
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, response.Body?.TotalItems);
        Assert.Equal([alpha.PublicId, beta.PublicId], response.Body!.Data!.Select(x => x.Id));
    }

    [FunctionalFact]
    public async Task GivenScopeAdminNotOwner_WhenGetScopePermissions_ThenForbidden()
    {
        // Given a Scope Admin with no standing in the scope (AF-32e)
        var scope = await SeedScopeAsync();
        await SeedScopePermissionAsync(scope, "Alpha");
        var stranger = await SeedScopeAdminAsync();
        Authorize(TestTokens.For(stranger.PublicId, (int)Roles.ScopeAdmin));

        // When
        var response = await Gateway.GetAsync<PaginatedOutput<ScopePermissionOutput>>(Route(scope.PublicId));

        // Then — refused outright rather than answered with an empty page
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenUserRole_WhenGetScopePermissions_ThenForbidden()
    {
        // Given a caller holding the User role (AF-32e, at the framework layer)
        var scope = await SeedScopeAsync();
        var caller = await SeedUserAsync(scope);
        Authorize(TestTokens.For(caller.PublicId, (int)Roles.User));

        // When
        var response = await Gateway.GetAsync<PaginatedOutput<ScopePermissionOutput>>(Route(scope.PublicId));

        // Then
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenUnknownScope_WhenGetScopePermissions_ThenNotFound()
    {
        // Given a scope id nobody holds (AF-31a, reused by the listing)
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.GetAsync<PaginatedOutput<ScopePermissionOutput>>(Route(Guid.NewGuid()));

        // Then
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenLogicallyDeletedScope_WhenGetScopePermissions_ThenNotFound()
    {
        // Given a logically deleted scope (AF-31a, reused by the listing)
        var scope = await SeedScopeAsync(isDeleted: true);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.GetAsync<PaginatedOutput<ScopePermissionOutput>>(Route(scope.PublicId));

        // Then
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenDeletedScopePermission_WhenGetScopePermissions_ThenItIsExcludedUnlessRequested()
    {
        // Given one active and one logically deleted permission (FR-SP-09)
        var scope = await SeedScopeAsync();
        var active = await SeedScopePermissionAsync(scope, "Alpha");
        await SeedScopePermissionAsync(scope, "Beta", isDeleted: true);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When — default, then explicitly including deleted
        var excluded = await Gateway.GetAsync<PaginatedOutput<ScopePermissionOutput>>(Route(scope.PublicId));
        var included = await Gateway.GetAsync<PaginatedOutput<ScopePermissionOutput>>(
            $"{Route(scope.PublicId)}&includeDeleted=true");

        // Then
        Assert.Equal(1, excluded.Body?.TotalItems);
        Assert.Equal(active.PublicId, Assert.Single(excluded.Body!.Data!).Id);
        Assert.Equal(2, included.Body?.TotalItems);
    }

    [FunctionalFact]
    public async Task GivenNameFilter_WhenGetScopePermissions_ThenOnlyMatchingPermissionsAreReturned()
    {
        // Given two permissions with distinct names, filtered case-insensitively
        var scope = await SeedScopeAsync();
        var billing = await SeedScopePermissionAsync(scope, "documents.read");
        await SeedScopePermissionAsync(scope, "documents.write");
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.GetAsync<PaginatedOutput<ScopePermissionOutput>>(
            $"{Route(scope.PublicId)}&name=READ");

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, response.Body?.TotalItems);
        Assert.Equal(billing.PublicId, Assert.Single(response.Body!.Data!).Id);
    }

    [FunctionalFact]
    public async Task GivenPageSize_WhenGetScopePermissions_ThenResultsArePaged()
    {
        // Given three permissions and a page size of two, ordered by name
        var scope = await SeedScopeAsync();
        await SeedScopePermissionAsync(scope, "Charlie");
        var alpha = await SeedScopePermissionAsync(scope, "Alpha");
        var bravo = await SeedScopePermissionAsync(scope, "Bravo");
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.GetAsync<PaginatedOutput<ScopePermissionOutput>>(
            $"/api/scopes/{scope.PublicId}/permissions?pageNumber=1&pageSize=2");

        // Then — the first page holds the two alphabetically first
        Assert.Equal(3, response.Body?.TotalItems);
        Assert.Equal([alpha.PublicId, bravo.PublicId], response.Body!.Data!.Select(x => x.Id));
    }

    [FunctionalFact]
    public async Task GivenForgedActingRoleInQueryString_WhenGetScopePermissions_ThenItIsIgnored()
    {
        // Given a Scope Admin claiming SystemAdmin in the query string: the query binds [FromQuery],
        // but ApplyActor runs after model binding and overwrites both acting fields from the token
        var scope = await SeedScopeAsync();
        var stranger = await SeedScopeAdminAsync();
        await SeedScopePermissionAsync(scope, "Alpha");
        Authorize(TestTokens.For(stranger.PublicId, (int)Roles.ScopeAdmin));

        // When
        var response = await Gateway.GetAsync<PaginatedOutput<ScopePermissionOutput>>(
            $"{Route(scope.PublicId)}&actingRole={(int)Roles.SystemAdmin}&actingPersonId={Guid.NewGuid()}");

        // Then — the forged role does not bypass the AF-32e ownership check
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenNoToken_WhenGetScopePermissions_ThenUnauthorized()
    {
        // Given no bearer token (precondition: the actor is authenticated)
        var scope = await SeedScopeAsync();

        // When
        var response = await Gateway.GetAsync<PaginatedOutput<ScopePermissionOutput>>(Route(scope.PublicId));

        // Then
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
