using System.Net;
using ArturRios.Configuration.Enums;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Query.Output;
using ArturRios.Heimdall.WebApi.Tests.Support;
using ArturRios.Output;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Functional;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Heimdall.WebApi.Tests;

[Collection(nameof(FunctionalCollection))]
public class ScopeControllerViewTests(PostgresFixture db) : WebApiTest<Program>(EnvironmentType.Local)
{
    private static string UniqueName() => $"scope-{Guid.NewGuid():N}";

    private async Task<Scope> SeedScopeAsync(string name)
    {
        await using var context = db.CreateContext();

        var scope = new Scope { PublicId = Guid.NewGuid(), Name = name, Description = "A scope" };

        context.Scopes.Add(scope);
        await context.SaveChangesAsync();

        return scope;
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
        // Person.ScopeId mirrors the membership row, as the application writes it — without
        // it the seeded User sits outside the per-scope uniqueness index.
        person.ScopeId = scope.Id;
        await context.SaveChangesAsync();

        return person;
    }

    // GET /api/scopes — list (SystemAdmin only)

    [FunctionalFact]
    public async Task GivenSystemAdmin_WhenGetScopesFilteredByName_ThenReturnsMatchingScope()
    {
        // Given a uniquely named scope so the shared database stays deterministic
        var scope = await SeedScopeAsync(UniqueName());
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.GetAsync<PaginatedOutput<ScopeOutput>>(
            $"/api/scopes?name={scope.Name}&pageNumber=1&pageSize=10");

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, response.Body?.TotalItems);
        Assert.Equal(scope.PublicId, Assert.Single(response.Body!.Data!).Id);
    }

    [FunctionalFact]
    public async Task GivenNonSystemAdmin_WhenGetScopes_ThenForbidden()
    {
        // Given
        Authorize(TestTokens.ForRole((int)Roles.User));

        // When
        var response = await Gateway.GetAsync<PaginatedOutput<ScopeOutput>>("/api/scopes?pageNumber=1&pageSize=10");

        // Then
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenNoToken_WhenGetScopes_ThenUnauthorized()
    {
        // Given no bearer token on the gateway

        // When
        var response = await Gateway.GetAsync<PaginatedOutput<ScopeOutput>>("/api/scopes?pageNumber=1&pageSize=10");

        // Then
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // GET /api/scopes/{id} — view by id (any authenticated actor)

    [FunctionalFact]
    public async Task GivenExistingScope_WhenGetScopeById_ThenReturnsScope()
    {
        // Given
        var scope = await SeedScopeAsync(UniqueName());
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.GetAsync<DataOutput<ScopeOutput?>>($"/api/scopes/{scope.PublicId}");

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(scope.PublicId, response.Body?.Data?.Id);
        Assert.Equal(scope.Name, response.Body?.Data?.Name);
    }

    [FunctionalFact]
    public async Task GivenUnknownScopeId_WhenGetScopeById_ThenNotFound()
    {
        // Given
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.GetAsync<DataOutput<ScopeOutput?>>($"/api/scopes/{Guid.NewGuid()}");

        // Then
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenScopeAdminOwningScope_WhenGetScopeById_ThenReturnsScope()
    {
        // Given a Scope Admin who owns the scope
        var scope = await SeedScopeAsync(UniqueName());
        var owner = await SeedScopeAdminAsync(ownedScope: scope);
        Authorize(TestTokens.For(owner.PublicId, (int)Roles.ScopeAdmin));

        // When
        var response = await Gateway.GetAsync<DataOutput<ScopeOutput?>>($"/api/scopes/{scope.PublicId}");

        // Then — response
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(scope.PublicId, response.Body?.Data?.Id);
        Assert.Contains(owner.PublicId, response.Body!.Data!.OwnerIds);

        // Then — the ownership row the rule read is in the database
        await using var context = db.CreateContext();
        Assert.True(await context.ScopeOwners
            .AsNoTracking()
            .AnyAsync(row => row.ScopeId == scope.Id && row.PersonId == owner.Id));
    }

    [FunctionalFact]
    public async Task GivenScopeAdminNotOwningScope_WhenGetScopeById_ThenForbidden()
    {
        // Given a Scope Admin who owns no part of the requested scope (AF-02b)
        var scope = await SeedScopeAsync(UniqueName());
        var admin = await SeedScopeAdminAsync();
        Authorize(TestTokens.For(admin.PublicId, (int)Roles.ScopeAdmin));

        // When
        var response = await Gateway.GetAsync<DataOutput<ScopeOutput?>>($"/api/scopes/{scope.PublicId}");

        // Then
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null(response.Body?.Data);
    }

    [FunctionalFact]
    public async Task GivenUserOfScope_WhenGetScopeById_ThenReturnsScope()
    {
        // Given a User who belongs to the scope
        var scope = await SeedScopeAsync(UniqueName());
        var member = await SeedUserAsync(scope);
        Authorize(TestTokens.For(member.PublicId, (int)Roles.User, scope.PublicId));

        // When
        var response = await Gateway.GetAsync<DataOutput<ScopeOutput?>>($"/api/scopes/{scope.PublicId}");

        // Then — response
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(scope.PublicId, response.Body?.Data?.Id);

        // Then — the membership row the rule read is in the database
        await using var context = db.CreateContext();
        Assert.True(await context.ScopeUsers
            .AsNoTracking()
            .AnyAsync(row => row.ScopeId == scope.Id && row.PersonId == member.Id));
    }

    [FunctionalFact]
    public async Task GivenUserOfAnotherScope_WhenGetScopeById_ThenForbidden()
    {
        // Given a User who belongs to a different scope than the one requested (AF-02b)
        var scope = await SeedScopeAsync(UniqueName());
        var otherScope = await SeedScopeAsync(UniqueName());
        var member = await SeedUserAsync(otherScope);
        Authorize(TestTokens.For(member.PublicId, (int)Roles.User, otherScope.PublicId));

        // When
        var response = await Gateway.GetAsync<DataOutput<ScopeOutput?>>($"/api/scopes/{scope.PublicId}");

        // Then
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenNoToken_WhenGetScopeById_ThenUnauthorized()
    {
        // Given a scope but no bearer token on the gateway
        var scope = await SeedScopeAsync(UniqueName());

        // When
        var response = await Gateway.GetAsync<DataOutput<ScopeOutput?>>($"/api/scopes/{scope.PublicId}");

        // Then
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
