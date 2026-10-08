using System.Net;
using ArturRios.Configuration.Enums;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Query.Output;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Heimdall.WebApi.Tests.Support;
using ArturRios.Output;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Functional;
using Application = ArturRios.Heimdall.Domain.Entities.Application;

namespace ArturRios.Heimdall.WebApi.Tests;

// Functional tests for GET /api/scopes/{scopeId}/applications/{id} (UC-17, FR-AP-04/09): the main
// flow for a System Admin and for the owning Scope Admin, AF-17a (unknown id, wrong scope, logically
// deleted), AF-17b (a co-owner of the scope who does not own the application), and the
// framework-level flows (403 for a User, 401 unauthenticated).
[Collection(nameof(FunctionalCollection))]
public class ApplicationControllerGetByIdTests(PostgresFixture db) : WebApiTest<Program>(EnvironmentType.Local)
{
    private static string Route(Guid scopeId, Guid id) => $"/api/scopes/{scopeId}/applications/{id}";

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
        // Person.ScopeId mirrors the membership row, as the application writes it — without
        // it the seeded User sits outside the per-scope uniqueness index.
        person.ScopeId = scope.Id;
        await context.SaveChangesAsync();

        return person;
    }

    private async Task<Application> SeedApplicationAsync(
        Scope scope, Person owner, string? name = null, bool isDeleted = false)
    {
        await using var context = db.CreateContext();
        var application = new Application
        {
            PublicId = Guid.NewGuid(),
            Name = name ?? $"app-{Guid.NewGuid():N}",
            IsDeleted = isDeleted,
            ScopeId = scope.Id,
            OwnerId = owner.Id
        };
        context.Applications.Add(application);
        await context.SaveChangesAsync();
        return application;
    }

    [FunctionalFact]
    public async Task GivenSystemAdmin_WhenGetApplicationById_ThenOkWithApplication()
    {
        // Given an application a System Admin does not own
        var scope = await SeedScopeAsync();
        var owner = await SeedScopeAdminAsync(ownedScope: scope);
        var application = await SeedApplicationAsync(scope, owner);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.GetAsync<DataOutput<ApplicationOutput?>>(
            Route(scope.PublicId, application.PublicId));

        // Then — the payload identifies the scope and owner by PublicId, never by internal id
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(application.PublicId, response.Body?.Data?.Id);
        Assert.Equal(application.Name, response.Body?.Data?.Name);
        Assert.Equal(scope.PublicId, response.Body?.Data?.ScopeId);
        Assert.Equal(owner.PublicId, response.Body?.Data?.OwnerId);
        Assert.False(response.Body?.Data?.IsDeleted);
    }

    [FunctionalFact]
    public async Task GivenOwningScopeAdmin_WhenGetApplicationById_ThenOk()
    {
        // Given the acting Scope Admin owns the application
        var scope = await SeedScopeAsync();
        var owner = await SeedScopeAdminAsync(ownedScope: scope);
        var application = await SeedApplicationAsync(scope, owner);
        Authorize(TestTokens.For(owner.PublicId, (int)Roles.ScopeAdmin));

        // When
        var response = await Gateway.GetAsync<DataOutput<ApplicationOutput?>>(
            Route(scope.PublicId, application.PublicId));

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(application.PublicId, response.Body?.Data?.Id);
    }

    [FunctionalFact]
    public async Task GivenScopeAdminWhoOwnsTheScopeButNotTheApplication_WhenGetApplicationById_ThenForbidden()
    {
        // Given a co-owner of the scope: owning the scope is not grounds to read another owner's
        // application (AF-17b)
        var scope = await SeedScopeAsync();
        var owner = await SeedScopeAdminAsync(ownedScope: scope);
        var coOwner = await SeedScopeAdminAsync(ownedScope: scope);
        var application = await SeedApplicationAsync(scope, owner);
        Authorize(TestTokens.For(coOwner.PublicId, (int)Roles.ScopeAdmin));

        // When
        var response = await Gateway.GetAsync<DataOutput<ApplicationOutput?>>(
            Route(scope.PublicId, application.PublicId));

        // Then
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenUserRole_WhenGetApplicationById_ThenForbidden()
    {
        // Given a caller holding the User role: FR-AP-03 lets them own no application, so the
        // endpoint's [RoleRequirement] refuses them
        var scope = await SeedScopeAsync();
        var owner = await SeedScopeAdminAsync(ownedScope: scope);
        var application = await SeedApplicationAsync(scope, owner);
        var caller = await SeedUserAsync(scope);
        Authorize(TestTokens.For(caller.PublicId, (int)Roles.User));

        // When
        var response = await Gateway.GetAsync<DataOutput<ApplicationOutput?>>(
            Route(scope.PublicId, application.PublicId));

        // Then
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenUnknownApplication_WhenGetApplicationById_ThenNotFound()
    {
        // Given an application id nobody holds (AF-17a)
        var scope = await SeedScopeAsync();
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.GetAsync<DataOutput<ApplicationOutput?>>(
            Route(scope.PublicId, Guid.NewGuid()));

        // Then
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenApplicationOfAnotherScope_WhenGetApplicationById_ThenNotFound()
    {
        // Given the application exists, but under a different scope than the path addresses (AF-17a)
        var scope = await SeedScopeAsync();
        var otherScope = await SeedScopeAsync();
        var owner = await SeedScopeAdminAsync(ownedScope: otherScope);
        var application = await SeedApplicationAsync(otherScope, owner);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.GetAsync<DataOutput<ApplicationOutput?>>(
            Route(scope.PublicId, application.PublicId));

        // Then
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenDeletedApplication_WhenGetApplicationById_ThenNotFound()
    {
        // Given a logically deleted application and no explicit request for it (FR-AP-09, AF-17a)
        var scope = await SeedScopeAsync();
        var owner = await SeedScopeAdminAsync(ownedScope: scope);
        var application = await SeedApplicationAsync(scope, owner, isDeleted: true);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.GetAsync<DataOutput<ApplicationOutput?>>(
            Route(scope.PublicId, application.PublicId));

        // Then
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenDeletedApplicationAndIncludeDeleted_WhenGetApplicationById_ThenOk()
    {
        // Given a logically deleted application explicitly requested (FR-AP-09)
        var scope = await SeedScopeAsync();
        var owner = await SeedScopeAdminAsync(ownedScope: scope);
        var application = await SeedApplicationAsync(scope, owner, isDeleted: true);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.GetAsync<DataOutput<ApplicationOutput?>>(
            $"{Route(scope.PublicId, application.PublicId)}?includeDeleted=true");

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Body?.Data?.IsDeleted);
    }

    [FunctionalFact]
    public async Task GivenNoToken_WhenGetApplicationById_ThenUnauthorized()
    {
        // Given no bearer token (precondition: the actor is authenticated)
        var scope = await SeedScopeAsync();

        // When
        var response = await Gateway.GetAsync<DataOutput<ApplicationOutput?>>(
            Route(scope.PublicId, Guid.NewGuid()));

        // Then
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenAnOwnerRemovedFromTheScope_WhenGetApplicationById_ThenForbidden()
    {
        // Given a Scope Admin still named as the application's owner, but no longer an owner of its
        // scope — UC-22 removed them, and FR-AP-03 says an owner must own the application's scope.
        // They still own another scope, so their token stays live.
        var scope = await SeedScopeAsync();
        var otherScope = await SeedScopeAsync();
        var owner = await SeedScopeAdminAsync(ownedScope: scope);

        await using (var context = db.CreateContext())
        {
            context.ScopeOwners.Add(new ScopeOwner { ScopeId = otherScope.Id, PersonId = owner.Id });
            await context.SaveChangesAsync();
        }

        var application = await SeedApplicationAsync(scope, owner);

        await using (var context = db.CreateContext())
        {
            context.ScopeOwners.Remove(new ScopeOwner { ScopeId = scope.Id, PersonId = owner.Id });
            await context.SaveChangesAsync();
        }

        Authorize(TestTokens.For(owner.PublicId, (int)Roles.ScopeAdmin, null, otherScope.PublicId));

        // When
        var read = await Gateway.GetAsync<DataOutput<ApplicationOutput?>>(
            Route(scope.PublicId, application.PublicId));
        var delete = await Gateway.DeleteAsync<DataOutput<object?>>(
            Route(scope.PublicId, application.PublicId));

        // Then — refused as any other non-owner is, and nothing deleted
        Assert.Equal(HttpStatusCode.Forbidden, read.StatusCode);
        Assert.Contains(ApplicationMessages.NotAuthorizedToViewApplication, read.Body!.Errors);
        Assert.Equal(HttpStatusCode.Forbidden, delete.StatusCode);

        await using var check = db.CreateContext();
        Assert.False(check.Applications.Single(x => x.Id == application.Id).IsDeleted);
    }
}
