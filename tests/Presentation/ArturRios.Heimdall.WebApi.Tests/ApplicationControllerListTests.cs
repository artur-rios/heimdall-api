using System.Net;
using ArturRios.Configuration.Enums;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Query.Output;
using ArturRios.Heimdall.WebApi.Tests.Support;
using ArturRios.Output;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Functional;
using Application = ArturRios.Heimdall.Domain.Entities.Application;

namespace ArturRios.Heimdall.WebApi.Tests;

// Functional tests for GET /api/scopes/{scopeId}/applications (UC-17, FR-AP-05/09): a System Admin
// sees every application in the scope, an owning Scope Admin only their own. Covers AF-17a (unknown
// or logically deleted scope), AF-17b (non-owning Scope Admin, and a User at the framework layer),
// the filters and pagination, include-deleted, the 401, and that a forged acting role in the query
// string is discarded.
[Collection(nameof(FunctionalCollection))]
public class ApplicationControllerListTests(PostgresFixture db) : WebApiTest<Program>(EnvironmentType.Local)
{
    private static string Route(Guid scopeId) => $"/api/scopes/{scopeId}/applications?pageNumber=1&pageSize=10";

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
    public async Task GivenSystemAdmin_WhenGetApplications_ThenOkWithEveryApplicationInTheScope()
    {
        // Given a scope whose two applications belong to different owners, plus one in another scope
        var scope = await SeedScopeAsync();
        var otherScope = await SeedScopeAsync();
        var owner = await SeedScopeAdminAsync(ownedScope: scope);
        var coOwner = await SeedScopeAdminAsync(ownedScope: scope);
        var outsideOwner = await SeedScopeAdminAsync(ownedScope: otherScope);
        var mine = await SeedApplicationAsync(scope, owner, "Alpha");
        var theirs = await SeedApplicationAsync(scope, coOwner, "Beta");
        await SeedApplicationAsync(otherScope, outsideOwner, "Gamma");
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.GetAsync<PaginatedOutput<ApplicationOutput>>(Route(scope.PublicId));

        // Then — both owners' applications, and nothing from the other scope
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, response.Body?.TotalItems);
        Assert.Equal([mine.PublicId, theirs.PublicId], response.Body!.Data!.Select(x => x.Id));
    }

    [FunctionalFact]
    public async Task GivenOwningScopeAdmin_WhenGetApplications_ThenOkWithOnlyTheirOwn()
    {
        // Given two co-owners of one scope, each with an application
        var scope = await SeedScopeAsync();
        var owner = await SeedScopeAdminAsync(ownedScope: scope);
        var coOwner = await SeedScopeAdminAsync(ownedScope: scope);
        var mine = await SeedApplicationAsync(scope, owner, "Alpha");
        await SeedApplicationAsync(scope, coOwner, "Beta");
        Authorize(TestTokens.For(owner.PublicId, (int)Roles.ScopeAdmin));

        // When
        var response = await Gateway.GetAsync<PaginatedOutput<ApplicationOutput>>(Route(scope.PublicId));

        // Then — the co-owner's application is not visible
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, response.Body?.TotalItems);
        Assert.Equal(mine.PublicId, Assert.Single(response.Body!.Data!).Id);
    }

    [FunctionalFact]
    public async Task GivenNonOwningScopeAdmin_WhenGetApplications_ThenForbidden()
    {
        // Given a Scope Admin with no standing in the scope (AF-17b)
        var scope = await SeedScopeAsync();
        var owner = await SeedScopeAdminAsync(ownedScope: scope);
        await SeedApplicationAsync(scope, owner);
        var stranger = await SeedScopeAdminAsync();
        Authorize(TestTokens.For(stranger.PublicId, (int)Roles.ScopeAdmin));

        // When
        var response = await Gateway.GetAsync<PaginatedOutput<ApplicationOutput>>(Route(scope.PublicId));

        // Then — refused outright rather than answered with an empty page
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenUserRole_WhenGetApplications_ThenForbidden()
    {
        // Given a caller holding the User role (AF-17b, at the framework layer)
        var scope = await SeedScopeAsync();
        var caller = await SeedUserAsync(scope);
        Authorize(TestTokens.For(caller.PublicId, (int)Roles.User));

        // When
        var response = await Gateway.GetAsync<PaginatedOutput<ApplicationOutput>>(Route(scope.PublicId));

        // Then
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenUnknownScope_WhenGetApplications_ThenNotFound()
    {
        // Given a scope id nobody holds (AF-17a)
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.GetAsync<PaginatedOutput<ApplicationOutput>>(Route(Guid.NewGuid()));

        // Then
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenLogicallyDeletedScope_WhenGetApplications_ThenNotFound()
    {
        // Given a logically deleted scope (AF-17a)
        var scope = await SeedScopeAsync(isDeleted: true);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.GetAsync<PaginatedOutput<ApplicationOutput>>(Route(scope.PublicId));

        // Then
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenDeletedApplication_WhenGetApplications_ThenItIsExcludedUnlessRequested()
    {
        // Given one active and one logically deleted application (FR-AP-09)
        var scope = await SeedScopeAsync();
        var owner = await SeedScopeAdminAsync(ownedScope: scope);
        var active = await SeedApplicationAsync(scope, owner, "Alpha");
        await SeedApplicationAsync(scope, owner, "Beta", isDeleted: true);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When — default, then explicitly including deleted
        var excluded = await Gateway.GetAsync<PaginatedOutput<ApplicationOutput>>(Route(scope.PublicId));
        var included = await Gateway.GetAsync<PaginatedOutput<ApplicationOutput>>(
            $"{Route(scope.PublicId)}&includeDeleted=true");

        // Then
        Assert.Equal(1, excluded.Body?.TotalItems);
        Assert.Equal(active.PublicId, Assert.Single(excluded.Body!.Data!).Id);
        Assert.Equal(2, included.Body?.TotalItems);
    }

    [FunctionalFact]
    public async Task GivenNameFilter_WhenGetApplications_ThenOnlyMatchingApplicationsAreReturned()
    {
        // Given two applications with distinct names, filtered case-insensitively
        var scope = await SeedScopeAsync();
        var owner = await SeedScopeAdminAsync(ownedScope: scope);
        var billing = await SeedApplicationAsync(scope, owner, "Billing Service");
        await SeedApplicationAsync(scope, owner, "Reporting Service");
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.GetAsync<PaginatedOutput<ApplicationOutput>>(
            $"{Route(scope.PublicId)}&name=BILLING");

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, response.Body?.TotalItems);
        Assert.Equal(billing.PublicId, Assert.Single(response.Body!.Data!).Id);
    }

    [FunctionalFact]
    public async Task GivenOwnerFilter_WhenGetApplications_ThenOnlyThatOwnersApplicationsAreReturned()
    {
        // Given a System Admin narrowing a scope to one of its two owners
        var scope = await SeedScopeAsync();
        var owner = await SeedScopeAdminAsync(ownedScope: scope);
        var coOwner = await SeedScopeAdminAsync(ownedScope: scope);
        var wanted = await SeedApplicationAsync(scope, owner, "Alpha");
        await SeedApplicationAsync(scope, coOwner, "Beta");
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.GetAsync<PaginatedOutput<ApplicationOutput>>(
            $"{Route(scope.PublicId)}&ownerId={owner.PublicId}");

        // Then
        Assert.Equal(1, response.Body?.TotalItems);
        Assert.Equal(wanted.PublicId, Assert.Single(response.Body!.Data!).Id);
    }

    [FunctionalFact]
    public async Task GivenPageSize_WhenGetApplications_ThenResultsArePaged()
    {
        // Given three applications and a page size of two, ordered by name
        var scope = await SeedScopeAsync();
        var owner = await SeedScopeAdminAsync(ownedScope: scope);
        await SeedApplicationAsync(scope, owner, "Charlie");
        var alpha = await SeedApplicationAsync(scope, owner, "Alpha");
        var bravo = await SeedApplicationAsync(scope, owner, "Bravo");
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.GetAsync<PaginatedOutput<ApplicationOutput>>(
            $"/api/scopes/{scope.PublicId}/applications?pageNumber=1&pageSize=2");

        // Then — the first page holds the two alphabetically first
        Assert.Equal(3, response.Body?.TotalItems);
        Assert.Equal([alpha.PublicId, bravo.PublicId], response.Body!.Data!.Select(x => x.Id));
    }

    [FunctionalFact]
    public async Task GivenForgedActingRoleInQueryString_WhenGetApplications_ThenItIsIgnored()
    {
        // Given a Scope Admin claiming SystemAdmin in the query string: the query binds [FromQuery],
        // but ApplyActor runs after model binding and overwrites both acting fields from the token
        var scope = await SeedScopeAsync();
        var owner = await SeedScopeAdminAsync(ownedScope: scope);
        var coOwner = await SeedScopeAdminAsync(ownedScope: scope);
        var mine = await SeedApplicationAsync(scope, owner, "Alpha");
        await SeedApplicationAsync(scope, coOwner, "Beta");
        Authorize(TestTokens.For(owner.PublicId, (int)Roles.ScopeAdmin));

        // When
        var response = await Gateway.GetAsync<PaginatedOutput<ApplicationOutput>>(
            $"{Route(scope.PublicId)}&actingRole={(int)Roles.SystemAdmin}&actingPersonId={coOwner.PublicId}");

        // Then — still only the caller's own application
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, response.Body?.TotalItems);
        Assert.Equal(mine.PublicId, Assert.Single(response.Body!.Data!).Id);
    }

    [FunctionalFact]
    public async Task GivenNoToken_WhenGetApplications_ThenUnauthorized()
    {
        // Given no bearer token (precondition: the actor is authenticated)
        var scope = await SeedScopeAsync();

        // When
        var response = await Gateway.GetAsync<PaginatedOutput<ApplicationOutput>>(Route(scope.PublicId));

        // Then
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
