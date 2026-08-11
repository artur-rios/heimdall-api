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
using Application = ArturRios.Heimdall.Domain.Entities.Application;

namespace ArturRios.Heimdall.WebApi.Tests;

// Functional tests for DELETE /api/scopes/{scopeId}/applications/{id}/hard (UC-20, FR-AP-08): the
// main flow for a System Admin — including an already logically deleted application — the scope and
// owner surviving the removal, AF-20a (unknown id, wrong scope, repeated call), and the framework
// flows the use case's single-actor list produces (403 for a Scope Admin who owns the application and
// for a User, 401 unauthenticated).
[Collection(nameof(FunctionalCollection))]
public class ApplicationControllerHardDeleteTests(PostgresFixture db) : WebApiTest<Program>(EnvironmentType.Local)
{
    private static string Route(Guid scopeId, Guid id) => $"/api/scopes/{scopeId}/applications/{id}/hard";

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

    private async Task<Application> SeedApplicationAsync(Scope scope, Person owner, bool isDeleted = false)
    {
        await using var context = db.CreateContext();
        var application = new Application
        {
            PublicId = Guid.NewGuid(),
            Name = $"app-{Guid.NewGuid():N}",
            IsDeleted = isDeleted,
            ScopeId = scope.Id,
            OwnerId = owner.Id
        };
        context.Applications.Add(application);
        await context.SaveChangesAsync();
        return application;
    }

    private async Task<bool> ExistsAsync(Guid publicId)
    {
        await using var context = db.CreateContext();
        return await context.Applications.AsNoTracking().AnyAsync(a => a.PublicId == publicId);
    }

    [FunctionalFact]
    public async Task GivenSystemAdmin_WhenHardDeleteApplication_ThenOkAndRowIsGone()
    {
        // Given an active application (UC-20 main flow)
        var scope = await SeedScopeAsync();
        var owner = await SeedScopeAdminAsync(ownedScope: scope);
        var application = await SeedApplicationAsync(scope, owner);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.DeleteAsync<DataOutput<HardDeleteApplicationCommandOutput?>>(
            Route(scope.PublicId, application.PublicId));

        // Then — the response carries the public identifier and the row is gone for good
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(application.PublicId, response.Body?.Data?.Id);
        Assert.False(await ExistsAsync(application.PublicId));
    }

    [FunctionalFact]
    public async Task GivenLogicallyDeletedApplication_WhenHardDeleteApplication_ThenOkAndRowIsGone()
    {
        // Given an application already logically deleted by UC-19 or by UC-04's scope cascade — the
        // lookup finds it regardless, so a cleanup pass can purge it
        var scope = await SeedScopeAsync(isDeleted: true);
        var owner = await SeedScopeAdminAsync(ownedScope: scope);
        var application = await SeedApplicationAsync(scope, owner, isDeleted: true);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.DeleteAsync<DataOutput<HardDeleteApplicationCommandOutput?>>(
            Route(scope.PublicId, application.PublicId));

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(await ExistsAsync(application.PublicId));
    }

    [FunctionalFact]
    public async Task GivenApplicationRemoved_WhenHardDeleteApplication_ThenScopeAndOwnerSurvive()
    {
        // Given an application whose foreign keys point outward at its scope and owner: removing it
        // cascades to neither
        var scope = await SeedScopeAsync();
        var owner = await SeedScopeAdminAsync(ownedScope: scope);
        var application = await SeedApplicationAsync(scope, owner);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.DeleteAsync<DataOutput<HardDeleteApplicationCommandOutput?>>(
            Route(scope.PublicId, application.PublicId));

        // Then — only the application is gone
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(await ExistsAsync(application.PublicId));

        await using var context = db.CreateContext();
        Assert.True(await context.Scopes.AsNoTracking().AnyAsync(s => s.PublicId == scope.PublicId));
        Assert.True(await context.Persons.AsNoTracking().AnyAsync(p => p.PublicId == owner.PublicId));
        Assert.True(await context.ScopeOwners.AsNoTracking()
            .AnyAsync(o => o.ScopeId == scope.Id && o.PersonId == owner.Id));
    }

    [FunctionalFact]
    public async Task GivenOwningScopeAdmin_WhenHardDeleteApplication_ThenForbidden()
    {
        // Given the Scope Admin who owns the application: UC-19 lets them logically delete it, UC-20
        // does not let them purge it — permanent removal is a System Admin operation
        var scope = await SeedScopeAsync();
        var owner = await SeedScopeAdminAsync(ownedScope: scope);
        var application = await SeedApplicationAsync(scope, owner);
        Authorize(TestTokens.For(owner.PublicId, (int)Roles.ScopeAdmin));

        // When
        var response = await Gateway.DeleteAsync<DataOutput<HardDeleteApplicationCommandOutput?>>(
            Route(scope.PublicId, application.PublicId));

        // Then — refused, and the row survives
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.True(await ExistsAsync(application.PublicId));
    }

    [FunctionalFact]
    public async Task GivenUserRole_WhenHardDeleteApplication_ThenForbidden()
    {
        // Given a caller holding the User role (UC-20's actor is the System Admin alone)
        var scope = await SeedScopeAsync();
        var owner = await SeedScopeAdminAsync(ownedScope: scope);
        var application = await SeedApplicationAsync(scope, owner);
        var caller = await SeedUserAsync(scope);
        Authorize(TestTokens.For(caller.PublicId, (int)Roles.User));

        // When
        var response = await Gateway.DeleteAsync<DataOutput<HardDeleteApplicationCommandOutput?>>(
            Route(scope.PublicId, application.PublicId));

        // Then
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.True(await ExistsAsync(application.PublicId));
    }

    [FunctionalFact]
    public async Task GivenUnknownApplication_WhenHardDeleteApplication_ThenNotFound()
    {
        // Given an application id nobody holds (AF-20a)
        var scope = await SeedScopeAsync();
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.DeleteAsync<DataOutput<HardDeleteApplicationCommandOutput?>>(
            Route(scope.PublicId, Guid.NewGuid()));

        // Then
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenApplicationOfAnotherScope_WhenHardDeleteApplication_ThenNotFound()
    {
        // Given the application exists, but under a different scope than the path addresses (AF-20a)
        var scope = await SeedScopeAsync();
        var otherScope = await SeedScopeAsync();
        var owner = await SeedScopeAdminAsync(ownedScope: otherScope);
        var application = await SeedApplicationAsync(otherScope, owner);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.DeleteAsync<DataOutput<HardDeleteApplicationCommandOutput?>>(
            Route(scope.PublicId, application.PublicId));

        // Then — refused, and the row survives
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.True(await ExistsAsync(application.PublicId));
    }

    [FunctionalFact]
    public async Task GivenApplicationHardDeletedTwice_WhenHardDeleteApplication_ThenSecondCallIsNotFound()
    {
        // Given the endpoint called twice: the removal leaves nothing to find, so UC-20 has no
        // idempotent path — unlike UC-19's AF-19b
        var scope = await SeedScopeAsync();
        var owner = await SeedScopeAdminAsync(ownedScope: scope);
        var application = await SeedApplicationAsync(scope, owner);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var first = await Gateway.DeleteAsync<DataOutput<HardDeleteApplicationCommandOutput?>>(
            Route(scope.PublicId, application.PublicId));
        var second = await Gateway.DeleteAsync<DataOutput<HardDeleteApplicationCommandOutput?>>(
            Route(scope.PublicId, application.PublicId));

        // Then
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, second.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenNoToken_WhenHardDeleteApplication_ThenUnauthorized()
    {
        // Given no bearer token (precondition: the actor is authenticated)
        var scope = await SeedScopeAsync();

        // When
        var response = await Gateway.DeleteAsync<DataOutput<HardDeleteApplicationCommandOutput?>>(
            Route(scope.PublicId, Guid.NewGuid()));

        // Then
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
