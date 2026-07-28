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
