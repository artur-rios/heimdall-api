using System.Net;
using ArturRios.Configuration.Enums;
using ArturRios.IdentityManager.Command.Output;
using ArturRios.IdentityManager.Domain.Entities;
using ArturRios.IdentityManager.Domain.Enums;
using ArturRios.IdentityManager.WebApi.Tests.Support;
using ArturRios.Output;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Functional;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.IdentityManager.WebApi.Tests;

// Functional tests for DELETE /api/scopes/{scopeId}/permissions/{id}/hard (UC-35, FR-SP-08): the main
// flow for a System Admin — including an already logically deleted permission — the scope surviving the
// removal, AF-35a (unknown id, wrong scope, repeated call), and the framework flows the use case's
// single-actor list produces (403 for a Scope Admin who owns the scope and for a User, 401
// unauthenticated).
[Collection(nameof(FunctionalCollection))]
public class ScopePermissionControllerHardDeleteTests(PostgresFixture db) : WebApiTest<Program>(EnvironmentType.Local)
{
    private static string Route(Guid scopeId, Guid id) => $"/api/scopes/{scopeId}/permissions/{id}/hard";

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
            ScopeId = scope.Id
        };
        context.ScopePermissions.Add(permission);
        await context.SaveChangesAsync();
        return permission;
    }

    private async Task<bool> ExistsAsync(Guid publicId)
    {
        await using var context = db.CreateContext();
        return await context.ScopePermissions.AsNoTracking().AnyAsync(p => p.PublicId == publicId);
    }

    [FunctionalFact]
    public async Task GivenSystemAdmin_WhenHardDeleteScopePermission_ThenOkAndRowIsGone()
    {
        // Given an active scope permission (UC-35 main flow)
        var scope = await SeedScopeAsync();
        var permission = await SeedScopePermissionAsync(scope);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.DeleteAsync<DataOutput<HardDeleteScopePermissionCommandOutput?>>(
            Route(scope.PublicId, permission.PublicId));

        // Then — the response carries the public identifier and the row is gone for good
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(permission.PublicId, response.Body?.Data?.Id);
        Assert.False(await ExistsAsync(permission.PublicId));
    }

    [FunctionalFact]
    public async Task GivenLogicallyDeletedScopePermission_WhenHardDeleteScopePermission_ThenOkAndRowIsGone()
    {
        // Given a permission already logically deleted by UC-34 or by UC-04's scope cascade — the
        // lookup finds it regardless, so a cleanup pass can purge it
        var scope = await SeedScopeAsync(isDeleted: true);
        var permission = await SeedScopePermissionAsync(scope, isDeleted: true);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.DeleteAsync<DataOutput<HardDeleteScopePermissionCommandOutput?>>(
            Route(scope.PublicId, permission.PublicId));

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(await ExistsAsync(permission.PublicId));
    }

    [FunctionalFact]
    public async Task GivenScopePermissionRemoved_WhenHardDeleteScopePermission_ThenScopeSurvives()
    {
        // Given a permission whose foreign key points outward at its scope: removing it cascades to
        // neither the scope nor any scope-owner link
        var scope = await SeedScopeAsync();
        var owner = await SeedScopeAdminAsync(ownedScope: scope);
        var permission = await SeedScopePermissionAsync(scope);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.DeleteAsync<DataOutput<HardDeleteScopePermissionCommandOutput?>>(
            Route(scope.PublicId, permission.PublicId));

        // Then — only the permission is gone
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(await ExistsAsync(permission.PublicId));

        await using var context = db.CreateContext();
        Assert.True(await context.Scopes.AsNoTracking().AnyAsync(s => s.PublicId == scope.PublicId));
        Assert.True(await context.Persons.AsNoTracking().AnyAsync(p => p.PublicId == owner.PublicId));
        Assert.True(await context.ScopeOwners.AsNoTracking()
            .AnyAsync(o => o.ScopeId == scope.Id && o.PersonId == owner.Id));
    }

    [FunctionalFact]
    public async Task GivenOwningScopeAdmin_WhenHardDeleteScopePermission_ThenForbidden()
    {
        // Given the Scope Admin who owns the scope: UC-34 lets them logically delete a permission,
        // UC-35 does not let them purge it — permanent removal is a System Admin operation
        var scope = await SeedScopeAsync();
        var admin = await SeedScopeAdminAsync(ownedScope: scope);
        var permission = await SeedScopePermissionAsync(scope);
        Authorize(TestTokens.For(admin.PublicId, (int)Roles.ScopeAdmin));

        // When
        var response = await Gateway.DeleteAsync<DataOutput<HardDeleteScopePermissionCommandOutput?>>(
            Route(scope.PublicId, permission.PublicId));

        // Then — refused, and the row survives
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.True(await ExistsAsync(permission.PublicId));
    }

    [FunctionalFact]
    public async Task GivenUserRole_WhenHardDeleteScopePermission_ThenForbidden()
    {
        // Given a caller holding the User role (UC-35's actor is the System Admin alone)
        var scope = await SeedScopeAsync();
        var permission = await SeedScopePermissionAsync(scope);
        var caller = await SeedUserAsync(scope);
        Authorize(TestTokens.For(caller.PublicId, (int)Roles.User));

        // When
        var response = await Gateway.DeleteAsync<DataOutput<HardDeleteScopePermissionCommandOutput?>>(
            Route(scope.PublicId, permission.PublicId));

        // Then
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.True(await ExistsAsync(permission.PublicId));
    }

    [FunctionalFact]
    public async Task GivenUnknownScopePermission_WhenHardDeleteScopePermission_ThenNotFound()
    {
        // Given a permission id nobody holds (AF-35a)
        var scope = await SeedScopeAsync();
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.DeleteAsync<DataOutput<HardDeleteScopePermissionCommandOutput?>>(
            Route(scope.PublicId, Guid.NewGuid()));

        // Then
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenScopePermissionOfAnotherScope_WhenHardDeleteScopePermission_ThenNotFound()
    {
        // Given the permission exists, but under a different scope than the path addresses (AF-35a)
        var scope = await SeedScopeAsync();
        var otherScope = await SeedScopeAsync();
        var permission = await SeedScopePermissionAsync(otherScope);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.DeleteAsync<DataOutput<HardDeleteScopePermissionCommandOutput?>>(
            Route(scope.PublicId, permission.PublicId));

        // Then — refused, and the row survives
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.True(await ExistsAsync(permission.PublicId));
    }

    [FunctionalFact]
    public async Task GivenScopePermissionHardDeletedTwice_WhenHardDeleteScopePermission_ThenSecondCallIsNotFound()
    {
        // Given the endpoint called twice: the removal leaves nothing to find, so UC-35 has no
        // idempotent path — unlike UC-34's AF-34b
        var scope = await SeedScopeAsync();
        var permission = await SeedScopePermissionAsync(scope);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var first = await Gateway.DeleteAsync<DataOutput<HardDeleteScopePermissionCommandOutput?>>(
            Route(scope.PublicId, permission.PublicId));
        var second = await Gateway.DeleteAsync<DataOutput<HardDeleteScopePermissionCommandOutput?>>(
            Route(scope.PublicId, permission.PublicId));

        // Then
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, second.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenNoToken_WhenHardDeleteScopePermission_ThenUnauthorized()
    {
        // Given no bearer token (precondition: the actor is authenticated)
        var scope = await SeedScopeAsync();

        // When
        var response = await Gateway.DeleteAsync<DataOutput<HardDeleteScopePermissionCommandOutput?>>(
            Route(scope.PublicId, Guid.NewGuid()));

        // Then
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
