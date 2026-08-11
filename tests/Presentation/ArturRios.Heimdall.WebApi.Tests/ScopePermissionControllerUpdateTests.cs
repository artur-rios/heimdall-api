using System.Net;
using ArturRios.Configuration.Enums;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.WebApi.Tests.Support;
using ArturRios.Output;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Functional;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Heimdall.WebApi.Tests;

// Functional tests for PUT /api/scopes/{scopeId}/permissions/{id} (UC-33, FR-SP-06): the main flow for
// a System Admin and for the owning Scope Admin, AF-33a (unknown id, wrong scope, logically deleted),
// AF-33e (a Scope Admin who does not own the scope), input validation reusing UC-31's messages, and
// the framework-level flows (403 for a User, 401 unauthenticated). A scope permission has no owner of
// its own, so UC-33 defines no owner-transfer flow.
[Collection(nameof(FunctionalCollection))]
public class ScopePermissionControllerUpdateTests(PostgresFixture db) : WebApiTest<Program>(EnvironmentType.Local)
{
    // A fixed, obviously-not-now timestamp, so "UpdatedAt was stamped" is a meaningful assertion.
    private static readonly DateTime Stamp = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static string Route(Guid scopeId, Guid id) => $"/api/scopes/{scopeId}/permissions/{id}";

    private static UpdateScopePermissionCommand Body(
        string? name = null, string? description = null, bool includeAsJwtClaim = false) => new()
    {
        Name = name ?? $"perm-{Guid.NewGuid():N}",
        Description = description,
        IncludeAsJwtClaim = includeAsJwtClaim
    };

    private async Task<Scope> SeedScopeAsync()
    {
        await using var context = db.CreateContext();
        var scope = new Scope { PublicId = Guid.NewGuid(), Name = $"scope-{Guid.NewGuid():N}" };
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

    private async Task<ScopePermission> SeedScopePermissionAsync(Scope scope, bool isDeleted = false)
    {
        await using var context = db.CreateContext();
        var permission = new ScopePermission
        {
            PublicId = Guid.NewGuid(),
            Name = $"perm-{Guid.NewGuid():N}",
            Description = "Original description.",
            IncludeAsJwtClaim = false,
            IsDeleted = isDeleted,
            ScopeId = scope.Id,
            UpdatedAt = Stamp
        };
        context.ScopePermissions.Add(permission);
        await context.SaveChangesAsync();
        return permission;
    }

    private async Task<ScopePermission> StoredAsync(Guid publicId)
    {
        await using var context = db.CreateContext();
        return await context.ScopePermissions.AsNoTracking().FirstAsync(p => p.PublicId == publicId);
    }

    [FunctionalFact]
    public async Task GivenSystemAdmin_WhenPutScopePermission_ThenOkAndRowIsUpdated()
    {
        // Given a scope permission a System Admin does not own
        var scope = await SeedScopeAsync();
        var permission = await SeedScopePermissionAsync(scope);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));
        var body = Body("documents.read", "Read documents", includeAsJwtClaim: true);

        // When
        var response = await Gateway.PutAsync<DataOutput<UpdateScopePermissionCommandOutput?>>(
            Route(scope.PublicId, permission.PublicId), body);

        // Then — response carries public identifiers only and echoes the updated fields
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(permission.PublicId, response.Body?.Data?.Id);
        Assert.Equal("documents.read", response.Body?.Data?.Name);
        Assert.Equal("Read documents", response.Body?.Data?.Description);
        Assert.True(response.Body?.Data?.IncludeAsJwtClaim);
        Assert.Equal(scope.PublicId, response.Body?.Data?.ScopeId);

        // Then — database state: renamed, flag flipped, and UpdatedAt stamped
        var stored = await StoredAsync(permission.PublicId);
        Assert.Equal("documents.read", stored.Name);
        Assert.Equal("Read documents", stored.Description);
        Assert.True(stored.IncludeAsJwtClaim);
        Assert.NotEqual(Stamp, stored.UpdatedAt);
    }

    [FunctionalFact]
    public async Task GivenOwningScopeAdmin_WhenPutScopePermission_ThenOkAndRowIsUpdated()
    {
        // Given the Scope Admin who owns the scope the permission belongs to (UC-33 step 3)
        var scope = await SeedScopeAsync();
        var admin = await SeedScopeAdminAsync(ownedScope: scope);
        var permission = await SeedScopePermissionAsync(scope);
        Authorize(TestTokens.For(admin.PublicId, (int)Roles.ScopeAdmin));

        // When
        var response = await Gateway.PutAsync<DataOutput<UpdateScopePermissionCommandOutput?>>(
            Route(scope.PublicId, permission.PublicId), Body("Renamed by owner"));

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Renamed by owner", (await StoredAsync(permission.PublicId)).Name);
    }

    [FunctionalFact]
    public async Task GivenScopeAdminNotOwner_WhenPutScopePermission_ThenForbidden()
    {
        // Given a Scope Admin who does NOT own the scope (AF-33e)
        var scope = await SeedScopeAsync();
        var permission = await SeedScopePermissionAsync(scope);
        var stranger = await SeedScopeAdminAsync();
        Authorize(TestTokens.For(stranger.PublicId, (int)Roles.ScopeAdmin));

        // When
        var response = await Gateway.PutAsync<DataOutput<UpdateScopePermissionCommandOutput?>>(
            Route(scope.PublicId, permission.PublicId), Body("Hijacked"));

        // Then — refused, and nothing moved
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        var stored = await StoredAsync(permission.PublicId);
        Assert.Equal(permission.Name, stored.Name);
        Assert.Equal(Stamp, stored.UpdatedAt);
    }

    [FunctionalFact]
    public async Task GivenUserRole_WhenPutScopePermission_ThenForbidden()
    {
        // Given a caller holding the User role: the endpoint's [RoleRequirement] refuses them
        var scope = await SeedScopeAsync();
        var permission = await SeedScopePermissionAsync(scope);
        var caller = await SeedUserAsync(scope);
        Authorize(TestTokens.For(caller.PublicId, (int)Roles.User));

        // When
        var response = await Gateway.PutAsync<DataOutput<UpdateScopePermissionCommandOutput?>>(
            Route(scope.PublicId, permission.PublicId), Body("Renamed"));

        // Then
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(permission.Name, (await StoredAsync(permission.PublicId)).Name);
    }

    [FunctionalFact]
    public async Task GivenUnknownScopePermission_WhenPutScopePermission_ThenNotFound()
    {
        // Given a permission id nobody holds (AF-33a)
        var scope = await SeedScopeAsync();
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.PutAsync<DataOutput<UpdateScopePermissionCommandOutput?>>(
            Route(scope.PublicId, Guid.NewGuid()), Body("Renamed"));

        // Then
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenScopePermissionOfAnotherScope_WhenPutScopePermission_ThenNotFound()
    {
        // Given the permission exists, but under a different scope than the path addresses (AF-33a)
        var scope = await SeedScopeAsync();
        var otherScope = await SeedScopeAsync();
        var permission = await SeedScopePermissionAsync(otherScope);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.PutAsync<DataOutput<UpdateScopePermissionCommandOutput?>>(
            Route(scope.PublicId, permission.PublicId), Body("Renamed"));

        // Then — refused, and the row is untouched
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(permission.Name, (await StoredAsync(permission.PublicId)).Name);
    }

    [FunctionalFact]
    public async Task GivenLogicallyDeletedScopePermission_WhenPutScopePermission_ThenNotFound()
    {
        // Given a logically deleted permission: the precondition excludes it (AF-33a)
        var scope = await SeedScopeAsync();
        var permission = await SeedScopePermissionAsync(scope, isDeleted: true);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.PutAsync<DataOutput<UpdateScopePermissionCommandOutput?>>(
            Route(scope.PublicId, permission.PublicId), Body("Renamed"));

        // Then
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(permission.Name, (await StoredAsync(permission.PublicId)).Name);
    }

    [FunctionalFact]
    public async Task GivenEmptyName_WhenPutScopePermission_ThenBadRequest()
    {
        // Given a body with no name (UC-33 step 2, reusing UC-31's AF-31d)
        var scope = await SeedScopeAsync();
        var permission = await SeedScopePermissionAsync(scope);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.PutAsync<DataOutput<UpdateScopePermissionCommandOutput?>>(
            Route(scope.PublicId, permission.PublicId), Body(string.Empty));

        // Then
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(permission.Name, (await StoredAsync(permission.PublicId)).Name);
    }

    [FunctionalFact]
    public async Task GivenOverlongDescription_WhenPutScopePermission_ThenBadRequest()
    {
        // Given a description over 500 characters (UC-33 step 2, reusing UC-31's AF-31d)
        var scope = await SeedScopeAsync();
        var permission = await SeedScopePermissionAsync(scope);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.PutAsync<DataOutput<UpdateScopePermissionCommandOutput?>>(
            Route(scope.PublicId, permission.PublicId), Body(description: new string('x', 501)));

        // Then
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenForgedActingRoleInBody_WhenPutScopePermission_ThenItIsIgnored()
    {
        // Given a Scope Admin claiming SystemAdmin in the body: ApplyActor runs after model binding
        // and overwrites both acting fields from the token, so the AF-33e refusal still stands
        var scope = await SeedScopeAsync();
        var permission = await SeedScopePermissionAsync(scope);
        var stranger = await SeedScopeAdminAsync();
        Authorize(TestTokens.For(stranger.PublicId, (int)Roles.ScopeAdmin));
        var body = Body("Hijacked");
        body.ActingRole = (int)Roles.SystemAdmin;
        body.ActingPersonId = Guid.NewGuid();

        // When
        var response = await Gateway.PutAsync<DataOutput<UpdateScopePermissionCommandOutput?>>(
            Route(scope.PublicId, permission.PublicId), body);

        // Then
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(permission.Name, (await StoredAsync(permission.PublicId)).Name);
    }

    [FunctionalFact]
    public async Task GivenNoToken_WhenPutScopePermission_ThenUnauthorized()
    {
        // Given no bearer token (precondition: the actor is authenticated)
        var scope = await SeedScopeAsync();

        // When
        var response = await Gateway.PutAsync<DataOutput<UpdateScopePermissionCommandOutput?>>(
            Route(scope.PublicId, Guid.NewGuid()), Body("Renamed"));

        // Then
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
