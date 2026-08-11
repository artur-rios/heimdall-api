using System.Net;
using ArturRios.Configuration.Enums;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.WebApi.Tests.Support;
using ArturRios.Output;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Functional;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Heimdall.WebApi.Tests;

// Functional tests for DELETE /api/scopes/{scopeId}/permissions/{id} (UC-34, FR-SP-07): the main flow
// for a System Admin and for the owning Scope Admin, AF-34a (unknown id, wrong scope), AF-34b (already
// deleted — seeded that way, by a repeated call, or sitting inside a logically deleted scope), AF-34e
// (a Scope Admin who does not own the scope), and the framework-level flows (403 for a User, 401
// unauthenticated).
[Collection(nameof(FunctionalCollection))]
public class ScopePermissionControllerDeleteTests(PostgresFixture db) : WebApiTest<Program>(EnvironmentType.Local)
{
    // A fixed, obviously-not-now timestamp, so "UpdatedAt was (not) stamped" is a meaningful assertion.
    private static readonly DateTime Stamp = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static string Route(Guid scopeId, Guid id) => $"/api/scopes/{scopeId}/permissions/{id}";

    private async Task<Scope> SeedScopeAsync(bool isDeleted = false)
    {
        await using var context = db.CreateContext();
        var scope = new Scope { PublicId = Guid.NewGuid(), Name = $"scope-{Guid.NewGuid():N}", IsDeleted = isDeleted };
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
            Description = "A test permission.",
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
    public async Task GivenSystemAdmin_WhenDeleteScopePermission_ThenOkAndRowIsFlagged()
    {
        // Given a scope permission a System Admin does not own
        var scope = await SeedScopeAsync();
        var permission = await SeedScopePermissionAsync(scope);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.DeleteAsync<DataOutput<DeleteScopePermissionCommandOutput?>>(
            Route(scope.PublicId, permission.PublicId));

        // Then — response carries the public identifier and the performed-now flag
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(permission.PublicId, response.Body?.Data?.Id);
        Assert.False(response.Body?.Data?.AlreadyDeleted);

        // Then — database state: flagged and UpdatedAt stamped
        var stored = await StoredAsync(permission.PublicId);
        Assert.True(stored.IsDeleted);
        Assert.NotEqual(Stamp, stored.UpdatedAt);
    }

    [FunctionalFact]
    public async Task GivenOwningScopeAdmin_WhenDeleteScopePermission_ThenOkAndRowIsFlagged()
    {
        // Given the Scope Admin who owns the scope the permission belongs to (UC-34 step 2)
        var scope = await SeedScopeAsync();
        var admin = await SeedScopeAdminAsync(ownedScope: scope);
        var permission = await SeedScopePermissionAsync(scope);
        Authorize(TestTokens.For(admin.PublicId, (int)Roles.ScopeAdmin));

        // When
        var response = await Gateway.DeleteAsync<DataOutput<DeleteScopePermissionCommandOutput?>>(
            Route(scope.PublicId, permission.PublicId));

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Body?.Data?.AlreadyDeleted);
        Assert.True((await StoredAsync(permission.PublicId)).IsDeleted);
    }

    [FunctionalFact]
    public async Task GivenAlreadyDeletedScopePermission_WhenDeleteScopePermission_ThenOkAndNothingChanges()
    {
        // Given a permission that is already logically deleted (AF-34b)
        var scope = await SeedScopeAsync();
        var permission = await SeedScopePermissionAsync(scope, isDeleted: true);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.DeleteAsync<DataOutput<DeleteScopePermissionCommandOutput?>>(
            Route(scope.PublicId, permission.PublicId));

        // Then — the same 200 as the main flow, and nothing was written
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Body?.Data?.AlreadyDeleted);

        var stored = await StoredAsync(permission.PublicId);
        Assert.True(stored.IsDeleted);
        Assert.Equal(Stamp, stored.UpdatedAt);
    }

    [FunctionalFact]
    public async Task GivenScopePermissionDeletedTwice_WhenDeleteScopePermission_ThenSecondCallReportsAlreadyDeleted()
    {
        // Given the endpoint called twice for the same permission (AF-34b: the call is idempotent)
        var scope = await SeedScopeAsync();
        var admin = await SeedScopeAdminAsync(ownedScope: scope);
        var permission = await SeedScopePermissionAsync(scope);
        Authorize(TestTokens.For(admin.PublicId, (int)Roles.ScopeAdmin));

        // When
        var first = await Gateway.DeleteAsync<DataOutput<DeleteScopePermissionCommandOutput?>>(
            Route(scope.PublicId, permission.PublicId));
        var deletedAt = (await StoredAsync(permission.PublicId)).UpdatedAt;
        var second = await Gateway.DeleteAsync<DataOutput<DeleteScopePermissionCommandOutput?>>(
            Route(scope.PublicId, permission.PublicId));

        // Then — both succeed identically; only the flag and the untouched timestamp differ
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.False(first.Body?.Data?.AlreadyDeleted);
        Assert.True(second.Body?.Data?.AlreadyDeleted);
        Assert.Equal(first.Body?.Messages, second.Body?.Messages);
        Assert.Equal(deletedAt, (await StoredAsync(permission.PublicId)).UpdatedAt);
    }

    [FunctionalFact]
    public async Task GivenDeletedScopePermissionInLogicallyDeletedScope_WhenDeleteScopePermission_ThenOkAndAlreadyDeleted()
    {
        // Given a deleted permission inside a logically deleted scope. UC-04 does not cascade to
        // permissions, so the two flags are independent; the handler does not consult the scope's
        // own state either way, it just finds the deleted row (AF-34b)
        var scope = await SeedScopeAsync(isDeleted: true);
        var permission = await SeedScopePermissionAsync(scope, isDeleted: true);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.DeleteAsync<DataOutput<DeleteScopePermissionCommandOutput?>>(
            Route(scope.PublicId, permission.PublicId));

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Body?.Data?.AlreadyDeleted);
        Assert.Equal(Stamp, (await StoredAsync(permission.PublicId)).UpdatedAt);
    }

    [FunctionalFact]
    public async Task GivenScopeAdminNotOwner_WhenDeleteScopePermission_ThenForbidden()
    {
        // Given a Scope Admin who does NOT own the scope (AF-34e)
        var scope = await SeedScopeAsync();
        var permission = await SeedScopePermissionAsync(scope);
        var stranger = await SeedScopeAdminAsync();
        Authorize(TestTokens.For(stranger.PublicId, (int)Roles.ScopeAdmin));

        // When
        var response = await Gateway.DeleteAsync<DataOutput<DeleteScopePermissionCommandOutput?>>(
            Route(scope.PublicId, permission.PublicId));

        // Then — refused, and the row is still active
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        var stored = await StoredAsync(permission.PublicId);
        Assert.False(stored.IsDeleted);
        Assert.Equal(Stamp, stored.UpdatedAt);
    }

    [FunctionalFact]
    public async Task GivenUserRole_WhenDeleteScopePermission_ThenForbidden()
    {
        // Given a caller holding the User role: the endpoint's [RoleRequirement] refuses them
        var scope = await SeedScopeAsync();
        var permission = await SeedScopePermissionAsync(scope);
        var caller = await SeedUserAsync(scope);
        Authorize(TestTokens.For(caller.PublicId, (int)Roles.User));

        // When
        var response = await Gateway.DeleteAsync<DataOutput<DeleteScopePermissionCommandOutput?>>(
            Route(scope.PublicId, permission.PublicId));

        // Then
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False((await StoredAsync(permission.PublicId)).IsDeleted);
    }

    [FunctionalFact]
    public async Task GivenUnknownScopePermission_WhenDeleteScopePermission_ThenNotFound()
    {
        // Given a permission id nobody holds (AF-34a)
        var scope = await SeedScopeAsync();
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.DeleteAsync<DataOutput<DeleteScopePermissionCommandOutput?>>(
            Route(scope.PublicId, Guid.NewGuid()));

        // Then
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenScopePermissionOfAnotherScope_WhenDeleteScopePermission_ThenNotFound()
    {
        // Given the permission exists, but under a different scope than the path addresses (AF-34a)
        var scope = await SeedScopeAsync();
        var otherScope = await SeedScopeAsync();
        var permission = await SeedScopePermissionAsync(otherScope);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.DeleteAsync<DataOutput<DeleteScopePermissionCommandOutput?>>(
            Route(scope.PublicId, permission.PublicId));

        // Then — refused, and the row is untouched
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.False((await StoredAsync(permission.PublicId)).IsDeleted);
    }

    [FunctionalFact]
    public async Task GivenNoToken_WhenDeleteScopePermission_ThenUnauthorized()
    {
        // Given no bearer token (precondition: the actor is authenticated)
        var scope = await SeedScopeAsync();

        // When
        var response = await Gateway.DeleteAsync<DataOutput<DeleteScopePermissionCommandOutput?>>(
            Route(scope.PublicId, Guid.NewGuid()));

        // Then
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
