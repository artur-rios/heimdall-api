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

// Functional tests for GET /api/scopes/{scopeId}/owners (UC-07, FR-PE-04): the main flow for a
// System Admin and an owning Scope Admin, AF-07a (unknown scope → 404), AF-07b (non-owner → 403),
// and the framework-level authorization flows (403 for a plain User, 401 unauthenticated).
[Collection(nameof(FunctionalCollection))]
public class PersonControllerListScopeOwnersTests(PostgresFixture db) : WebApiTest<Program>(EnvironmentType.Local)
{
    private async Task<Scope> SeedScopeAsync()
    {
        await using var context = db.CreateContext();
        var scope = new Scope { PublicId = Guid.NewGuid(), Name = $"scope-{Guid.NewGuid():N}" };
        context.Scopes.Add(scope);
        await context.SaveChangesAsync();
        return scope;
    }

    private async Task<Person> SeedUserAsync(Scope scope)
    {
        await using var context = db.CreateContext();
        var person = new Person
        {
            PublicId = Guid.NewGuid(),
            Name = "Member",
            Email = $"user-{Guid.NewGuid():N}@test.local",
            RoleId = (long)Roles.User
        };
        context.Persons.Add(person);
        await context.SaveChangesAsync();

        context.ScopeUsers.Add(new ScopeUser { ScopeId = scope.Id, PersonId = person.Id });
        await context.SaveChangesAsync();

        return person;
    }

    private async Task<Person> SeedScopeAdminAsync(Scope? ownedScope = null, string name = "Admin")
    {
        await using var context = db.CreateContext();
        var person = new Person
        {
            PublicId = Guid.NewGuid(),
            Name = name,
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
    public async Task GivenSystemAdmin_WhenGetScopeOwners_ThenReturnsOnlyThatScopesOwners()
    {
        // Given a scope with one owner and one User, plus an owner of another scope
        var scope = await SeedScopeAsync();
        var otherScope = await SeedScopeAsync();
        var owner = await SeedScopeAdminAsync(ownedScope: scope);
        await SeedScopeAdminAsync(ownedScope: otherScope);
        var member = await SeedUserAsync(scope);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.GetAsync<PaginatedOutput<PersonOutput>>(
            $"/api/scopes/{scope.PublicId}/owners?pageNumber=1&pageSize=10");

        // Then — only the scope's owner, not its User and not the other scope's owner
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, response.Body?.TotalItems);
        var returned = Assert.Single(response.Body!.Data!);
        Assert.Equal(owner.PublicId, returned.Id);
        Assert.NotEqual(member.PublicId, returned.Id);
        Assert.Contains(scope.PublicId, returned.OwnedScopeIds);
    }

    [FunctionalFact]
    public async Task GivenOwnerScopeAdmin_WhenGetScopeOwners_ThenReturnsCoOwners()
    {
        // Given two ScopeAdmins owning the same scope
        var scope = await SeedScopeAsync();
        var owner = await SeedScopeAdminAsync(ownedScope: scope, name: "Ana");
        await SeedScopeAdminAsync(ownedScope: scope, name: "Bruno");
        Authorize(TestTokens.For((int)owner.Id, (int)Roles.ScopeAdmin));

        // When
        var response = await Gateway.GetAsync<PaginatedOutput<PersonOutput>>(
            $"/api/scopes/{scope.PublicId}/owners?pageNumber=1&pageSize=10");

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, response.Body?.TotalItems);
    }

    [FunctionalFact]
    public async Task GivenNameFilter_WhenGetScopeOwners_ThenReturnsMatchingOwnerOnly()
    {
        // Given two owners of the scope
        var scope = await SeedScopeAsync();
        var ana = await SeedScopeAdminAsync(ownedScope: scope, name: "Ana");
        await SeedScopeAdminAsync(ownedScope: scope, name: "Bruno");
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.GetAsync<PaginatedOutput<PersonOutput>>(
            $"/api/scopes/{scope.PublicId}/owners?name=ana&pageNumber=1&pageSize=10");

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(ana.PublicId, Assert.Single(response.Body!.Data!).Id);
    }

    [FunctionalFact]
    public async Task GivenScopeAdminNotOwningScope_WhenGetScopeOwners_ThenForbidden()
    {
        // Given a ScopeAdmin who does not own the scope (AF-07b)
        var scope = await SeedScopeAsync();
        await SeedScopeAdminAsync(ownedScope: scope);
        var outsider = await SeedScopeAdminAsync();
        Authorize(TestTokens.For((int)outsider.Id, (int)Roles.ScopeAdmin));

        // When
        var response = await Gateway.GetAsync<PaginatedOutput<PersonOutput>>(
            $"/api/scopes/{scope.PublicId}/owners?pageNumber=1&pageSize=10");

        // Then
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenUnknownScope_WhenGetScopeOwners_ThenNotFound()
    {
        // Given (AF-07a)
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.GetAsync<PaginatedOutput<PersonOutput>>(
            $"/api/scopes/{Guid.NewGuid()}/owners?pageNumber=1&pageSize=10");

        // Then
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenPlainUserCaller_WhenGetScopeOwners_ThenForbidden()
    {
        // Given a User, whom the role gate rejects before the handler runs
        var scope = await SeedScopeAsync();
        Authorize(TestTokens.ForRole((int)Roles.User));

        // When
        var response = await Gateway.GetAsync<PaginatedOutput<PersonOutput>>(
            $"/api/scopes/{scope.PublicId}/owners?pageNumber=1&pageSize=10");

        // Then
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenNoToken_WhenGetScopeOwners_ThenUnauthorized()
    {
        // Given a scope but no bearer token on the gateway
        var scope = await SeedScopeAsync();

        // When
        var response = await Gateway.GetAsync<PaginatedOutput<PersonOutput>>(
            $"/api/scopes/{scope.PublicId}/owners?pageNumber=1&pageSize=10");

        // Then
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
