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

// Functional tests for GET /api/scopes/{scopeId}/persons (UC-07, FR-PE-04): the main flow for a
// System Admin and an owning Scope Admin, AF-07a (unknown scope → 404), AF-07b (non-owner → 403),
// and the framework-level authorization flows (403 for a plain User, 401 unauthenticated).
[Collection(nameof(FunctionalCollection))]
public class PersonControllerListScopePersonsTests(PostgresFixture db) : WebApiTest<Program>(EnvironmentType.Local)
{
    private async Task<Scope> SeedScopeAsync()
    {
        await using var context = db.CreateContext();
        var scope = new Scope { PublicId = Guid.NewGuid(), Name = $"scope-{Guid.NewGuid():N}" };
        context.Scopes.Add(scope);
        await context.SaveChangesAsync();
        return scope;
    }

    private async Task<Person> SeedUserAsync(Scope scope, string name)
    {
        await using var context = db.CreateContext();
        var person = new Person
        {
            PublicId = Guid.NewGuid(),
            Name = name,
            Email = $"user-{Guid.NewGuid():N}@test.local",
            RoleId = (long)Roles.User
        };
        context.Persons.Add(person);
        await context.SaveChangesAsync();

        context.ScopeUsers.Add(new ScopeUser { ScopeId = scope.Id, PersonId = person.Id });
        await context.SaveChangesAsync();

        return person;
    }

    private async Task<Person> SeedScopeAdminAsync(Scope? ownedScope = null)
    {
        await using var context = db.CreateContext();
        var person = new Person
        {
            PublicId = Guid.NewGuid(),
            Name = "Admin",
            Email = $"admin-{Guid.NewGuid():N}@test.local",
            RoleId = (long)Roles.ScopeAdmin,
            EmailVerified = true
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
    public async Task GivenSystemAdmin_WhenGetScopePersons_ThenReturnsOnlyThatScopesUsers()
    {
        // Given two scopes, each with a User, and an owner on the first
        var scope = await SeedScopeAsync();
        var otherScope = await SeedScopeAsync();
        var member = await SeedUserAsync(scope, "Ana");
        await SeedUserAsync(otherScope, "Carla");
        var owner = await SeedScopeAdminAsync(ownedScope: scope);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.GetAsync<PaginatedOutput<PersonOutput>>(
            $"/api/scopes/{scope.PublicId}/persons?pageNumber=1&pageSize=10");

        // Then — only the scope's User, not its owner and not the other scope's User
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, response.Body?.TotalItems);
        var returned = Assert.Single(response.Body!.Data!);
        Assert.Equal(member.PublicId, returned.Id);
        Assert.NotEqual(owner.PublicId, returned.Id);
        Assert.Equal(scope.PublicId, returned.ScopeId);
    }

    [FunctionalFact]
    public async Task GivenOwnerScopeAdmin_WhenGetScopePersons_ThenReturnsUsers()
    {
        // Given a ScopeAdmin who owns the scope
        var scope = await SeedScopeAsync();
        var owner = await SeedScopeAdminAsync(ownedScope: scope);
        await SeedUserAsync(scope, "Ana");
        Authorize(TestTokens.For((int)owner.Id, (int)Roles.ScopeAdmin));

        // When
        var response = await Gateway.GetAsync<PaginatedOutput<PersonOutput>>(
            $"/api/scopes/{scope.PublicId}/persons?pageNumber=1&pageSize=10");

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, response.Body?.TotalItems);
    }

    [FunctionalFact]
    public async Task GivenNameFilter_WhenGetScopePersons_ThenReturnsMatchingUserOnly()
    {
        // Given two Users in the scope
        var scope = await SeedScopeAsync();
        var ana = await SeedUserAsync(scope, "Ana");
        await SeedUserAsync(scope, "Bruno");
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.GetAsync<PaginatedOutput<PersonOutput>>(
            $"/api/scopes/{scope.PublicId}/persons?name=ana&pageNumber=1&pageSize=10");

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(ana.PublicId, Assert.Single(response.Body!.Data!).Id);
    }

    [FunctionalFact]
    public async Task GivenForgedActorInQueryString_WhenGetScopePersons_ThenTokenActorWins()
    {
        // Given a non-owning ScopeAdmin trying to impersonate a System Admin through the query string
        var scope = await SeedScopeAsync();
        var admin = await SeedScopeAdminAsync();
        Authorize(TestTokens.For((int)admin.Id, (int)Roles.ScopeAdmin));

        // When
        var response = await Gateway.GetAsync<PaginatedOutput<PersonOutput>>(
            $"/api/scopes/{scope.PublicId}/persons?actingRole=1&actingPersonId=1&pageNumber=1&pageSize=10");

        // Then — the forged values are discarded and the real actor is rejected (AF-07b)
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenScopeAdminNotOwningScope_WhenGetScopePersons_ThenForbidden()
    {
        // Given a ScopeAdmin who does not own the scope (AF-07b)
        var scope = await SeedScopeAsync();
        var admin = await SeedScopeAdminAsync();
        Authorize(TestTokens.For((int)admin.Id, (int)Roles.ScopeAdmin));

        // When
        var response = await Gateway.GetAsync<PaginatedOutput<PersonOutput>>(
            $"/api/scopes/{scope.PublicId}/persons?pageNumber=1&pageSize=10");

        // Then
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenUnknownScope_WhenGetScopePersons_ThenNotFound()
    {
        // Given (AF-07a)
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.GetAsync<PaginatedOutput<PersonOutput>>(
            $"/api/scopes/{Guid.NewGuid()}/persons?pageNumber=1&pageSize=10");

        // Then
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenPlainUserCaller_WhenGetScopePersons_ThenForbidden()
    {
        // Given a User, whom the role gate rejects before the handler runs
        var scope = await SeedScopeAsync();
        Authorize(TestTokens.ForRole((int)Roles.User));

        // When
        var response = await Gateway.GetAsync<PaginatedOutput<PersonOutput>>(
            $"/api/scopes/{scope.PublicId}/persons?pageNumber=1&pageSize=10");

        // Then
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenNoToken_WhenGetScopePersons_ThenUnauthorized()
    {
        // Given a scope but no bearer token on the gateway
        var scope = await SeedScopeAsync();

        // When
        var response = await Gateway.GetAsync<PaginatedOutput<PersonOutput>>(
            $"/api/scopes/{scope.PublicId}/persons?pageNumber=1&pageSize=10");

        // Then
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
