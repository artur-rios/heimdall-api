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

// Functional tests for UC-31 (Create Scope Permission): the main flow for a System Admin and for an
// owning Scope Admin, AF-31a (unknown or logically deleted scope), AF-31d (invalid input), AF-31e (a
// Scope Admin who does not own the scope), the User-role refusal at the [RoleRequirement] gate, the
// anonymous 401, and the duplicate-name boundary the design records. A scope permission has no owner
// of its own, so there is no equivalent of UC-16's owner-validation flows.
[Collection(nameof(FunctionalCollection))]
public class ScopePermissionControllerCreateTests(PostgresFixture db) : WebApiTest<Program>(EnvironmentType.Local)
{
    private static CreateScopePermissionCommand Command(
        string? name = null, string? description = null, bool includeAsJwtClaim = false) => new()
    {
        Name = name ?? $"perm-{Guid.NewGuid():N}",
        Description = description,
        IncludeAsJwtClaim = includeAsJwtClaim
    };

    private static string Route(Guid scopeId) => $"/api/scopes/{scopeId}/permissions";

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

    private async Task<Person> SeedUserAsync(Scope scope, bool isDeleted = false)
    {
        await using var context = db.CreateContext();
        var person = new Person
        {
            PublicId = Guid.NewGuid(), Name = "User",
            Email = $"user-{Guid.NewGuid():N}@test.local",
            RoleId = (long)Roles.User, EmailVerified = true, IsDeleted = isDeleted
        };
        context.Persons.Add(person);
        await context.SaveChangesAsync();

        context.ScopeUsers.Add(new ScopeUser { ScopeId = scope.Id, PersonId = person.Id });
        await context.SaveChangesAsync();

        return person;
    }

    private async Task<Person> SeedScopeAdminAsync(Scope? ownedScope = null, bool isDeleted = false)
    {
        await using var context = db.CreateContext();
        var person = new Person
        {
            PublicId = Guid.NewGuid(), Name = "Admin",
            Email = $"admin-{Guid.NewGuid():N}@test.local",
            RoleId = (long)Roles.ScopeAdmin, EmailVerified = true, IsDeleted = isDeleted
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

    [FunctionalFact]
    public async Task GivenSystemAdmin_WhenPostScopePermissions_ThenScopePermissionIsCreated()
    {
        // Given a scope (FR-SP-01/02). The name is unique per run so the row lookup below is not
        // confused by another test's permission in the shared database.
        var scope = await SeedScopeAsync();
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));
        var command = Command($"documents.read-{Guid.NewGuid():N}", "Read documents", includeAsJwtClaim: true);

        // When
        var response = await Gateway.PostAsync<DataOutput<CreateScopePermissionCommandOutput?>>(
            Route(scope.PublicId), command);

        // Then — response carries public identifiers only and echoes the submitted fields
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(command.Name, response.Body?.Data?.Name);
        Assert.Equal(command.Description, response.Body?.Data?.Description);
        Assert.True(response.Body?.Data?.IncludeAsJwtClaim);
        Assert.Equal(scope.PublicId, response.Body?.Data?.ScopeId);

        // Then — the row points at the scope, carries the flag, and is active
        await using var context = db.CreateContext();
        var permission = await context.ScopePermissions.AsNoTracking()
            .FirstAsync(p => p.Name == command.Name);
        Assert.Equal(scope.Id, permission.ScopeId);
        Assert.True(permission.IncludeAsJwtClaim);
        Assert.False(permission.IsDeleted);
        Assert.Equal(response.Body?.Data?.Id, permission.PublicId);
    }

    [FunctionalFact]
    public async Task GivenOwningScopeAdmin_WhenPostScopePermissions_ThenScopePermissionIsCreated()
    {
        // Given a ScopeAdmin who owns the scope (matrix: "owning Scope Admin")
        var scope = await SeedScopeAsync();
        var admin = await SeedScopeAdminAsync(ownedScope: scope);
        Authorize(TestTokens.For(admin.PublicId, (int)Roles.ScopeAdmin));
        var command = Command();

        // When
        var response = await Gateway.PostAsync<DataOutput<CreateScopePermissionCommandOutput?>>(
            Route(scope.PublicId), command);

        // Then
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(command.Name, response.Body?.Data?.Name);
    }

    [FunctionalFact]
    public async Task GivenScopeAdminNotOwner_WhenPostScopePermissions_ThenForbiddenAndNothingIsCreated()
    {
        // Given a ScopeAdmin who does NOT own the scope (AF-31e)
        var scope = await SeedScopeAsync();
        await SeedScopeAdminAsync(ownedScope: scope);
        var stranger = await SeedScopeAdminAsync();
        Authorize(TestTokens.For(stranger.PublicId, (int)Roles.ScopeAdmin));
        var command = Command();

        // When
        var response = await Gateway.PostAsync<DataOutput<CreateScopePermissionCommandOutput?>>(
            Route(scope.PublicId), command);

        // Then — refused, and no row was written
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        await using var context = db.CreateContext();
        Assert.False(await context.ScopePermissions.AnyAsync(p => p.Name == command.Name));
    }

    [FunctionalFact]
    public async Task GivenUserRole_WhenPostScopePermissions_ThenForbidden()
    {
        // Given a caller holding the User role: the endpoint's [RoleRequirement] refuses them, since
        // a User has no standing to manage a scope's permissions
        var scope = await SeedScopeAsync();
        var caller = await SeedUserAsync(scope);
        Authorize(TestTokens.For(caller.PublicId, (int)Roles.User));
        var command = Command();

        // When
        var response = await Gateway.PostAsync<DataOutput<CreateScopePermissionCommandOutput?>>(
            Route(scope.PublicId), command);

        // Then
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        await using var context = db.CreateContext();
        Assert.False(await context.ScopePermissions.AnyAsync(p => p.Name == command.Name));
    }

    [FunctionalFact]
    public async Task GivenMissingScope_WhenPostScopePermissions_ThenNotFound()
    {
        // Given a scope id nobody holds (AF-31a)
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.PostAsync<DataOutput<CreateScopePermissionCommandOutput?>>(
            Route(Guid.NewGuid()), Command());

        // Then
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenLogicallyDeletedScope_WhenPostScopePermissions_ThenNotFound()
    {
        // Given a logically deleted scope (AF-31a)
        var scope = await SeedScopeAsync(isDeleted: true);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.PostAsync<DataOutput<CreateScopePermissionCommandOutput?>>(
            Route(scope.PublicId), Command());

        // Then
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenEmptyName_WhenPostScopePermissions_ThenBadRequest()
    {
        // Given a command with no name (AF-31d)
        var scope = await SeedScopeAsync();
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.PostAsync<DataOutput<CreateScopePermissionCommandOutput?>>(
            Route(scope.PublicId), Command(name: string.Empty));

        // Then
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        await using var context = db.CreateContext();
        Assert.False(await context.ScopePermissions.AnyAsync(p => p.Name == string.Empty));
    }

    [FunctionalFact]
    public async Task GivenOverlongDescription_WhenPostScopePermissions_ThenBadRequest()
    {
        // Given a description over 500 characters (AF-31d)
        var scope = await SeedScopeAsync();
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.PostAsync<DataOutput<CreateScopePermissionCommandOutput?>>(
            Route(scope.PublicId), Command(description: new string('x', 501)));

        // Then
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenNoToken_WhenPostScopePermissions_ThenUnauthorized()
    {
        // Given no bearer token (precondition: the actor is authenticated)
        var scope = await SeedScopeAsync();

        // When
        var response = await Gateway.PostAsync<DataOutput<CreateScopePermissionCommandOutput?>>(
            Route(scope.PublicId), Command());

        // Then
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenDuplicateName_WhenPostScopePermissions_ThenBothAreCreated()
    {
        // Given a permission already registered under a name: no requirement makes names unique. The
        // name is unique per run so the count below is not inflated by another test's permission.
        var scope = await SeedScopeAsync();
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));
        var command = Command($"documents.read-{Guid.NewGuid():N}");
        var first = await Gateway.PostAsync<DataOutput<CreateScopePermissionCommandOutput?>>(
            Route(scope.PublicId), command);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        // When posting the same name again
        var response = await Gateway.PostAsync<DataOutput<CreateScopePermissionCommandOutput?>>(
            Route(scope.PublicId), command);

        // Then — both exist, distinguished by their public identifiers
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotEqual(first.Body?.Data?.Id, response.Body?.Data?.Id);

        await using var context = db.CreateContext();
        Assert.Equal(2, await context.ScopePermissions.CountAsync(p => p.Name == command.Name));
    }
}
