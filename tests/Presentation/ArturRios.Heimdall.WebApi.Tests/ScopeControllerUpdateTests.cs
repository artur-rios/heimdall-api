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

[Collection(nameof(FunctionalCollection))]
public class ScopeControllerUpdateTests(PostgresFixture db) : WebApiTest<Program>(EnvironmentType.Local)
{
    private static string UniqueName() => $"scope-{Guid.NewGuid():N}";

    [FunctionalFact]
    public async Task GivenSystemAdminAndValidPayload_WhenPutScope_ThenScopeIsUpdated()
    {
        // Given
        var scope = await Seeds.ScopeAsync(db, new ScopeOptions { Name = UniqueName(), Description = "Original" });
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));
        var newName = UniqueName();

        // When
        var response = await Gateway.PutAsync<DataOutput<UpdateScopeCommandOutput?>>(
            $"/api/scopes/{scope.PublicId}",
            new UpdateScopeCommand { Name = newName, Description = "Updated" });

        // Then — response
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(newName, response.Body?.Data?.Name);
        Assert.Equal("Updated", response.Body?.Data?.Description);

        // Then — database state
        await using var context = db.CreateContext();
        var persisted = await context.Scopes.AsNoTracking().FirstAsync(x => x.PublicId == scope.PublicId);
        Assert.Equal(newName, persisted.Name);
        Assert.Equal("Updated", persisted.Description);
    }

    [FunctionalFact]
    public async Task GivenUnknownScopeId_WhenPutScope_ThenNotFound()
    {
        // Given
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.PutAsync<DataOutput<UpdateScopeCommandOutput?>>(
            $"/api/scopes/{Guid.NewGuid()}",
            new UpdateScopeCommand { Name = UniqueName() });

        // Then
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenLogicallyDeletedScope_WhenPutScope_ThenNotFound()
    {
        // Given
        var scope = await Seeds.ScopeAsync(db, new ScopeOptions { Name = UniqueName(), Description = "Original", IsDeleted = true });
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.PutAsync<DataOutput<UpdateScopeCommandOutput?>>(
            $"/api/scopes/{scope.PublicId}",
            new UpdateScopeCommand { Name = UniqueName() });

        // Then
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenNameOfAnotherScope_WhenPutScope_ThenConflict()
    {
        // Given two scopes; the target tries to take the other's name
        var target = await Seeds.ScopeAsync(db, new ScopeOptions { Name = UniqueName(), Description = "Original" });
        var other = await Seeds.ScopeAsync(db, new ScopeOptions { Name = UniqueName(), Description = "Original" });
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.PutAsync<DataOutput<UpdateScopeCommandOutput?>>(
            $"/api/scopes/{target.PublicId}",
            new UpdateScopeCommand { Name = other.Name });

        // Then — response
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        // Then — target's name is unchanged in the database
        await using var context = db.CreateContext();
        var persisted = await context.Scopes.AsNoTracking().FirstAsync(x => x.PublicId == target.PublicId);
        Assert.Equal(target.Name, persisted.Name);
    }

    [FunctionalFact]
    public async Task GivenEmptyName_WhenPutScope_ThenBadRequest()
    {
        // Given
        var scope = await Seeds.ScopeAsync(db, new ScopeOptions { Name = UniqueName(), Description = "Original" });
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.PutAsync<DataOutput<UpdateScopeCommandOutput?>>(
            $"/api/scopes/{scope.PublicId}",
            new UpdateScopeCommand { Name = string.Empty });

        // Then
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenNonSystemAdmin_WhenPutScope_ThenForbidden()
    {
        // Given
        var scope = await Seeds.ScopeAsync(db, new ScopeOptions { Name = UniqueName(), Description = "Original" });
        Authorize(TestTokens.ForRole((int)Roles.User));

        // When
        var response = await Gateway.PutAsync<DataOutput<UpdateScopeCommandOutput?>>(
            $"/api/scopes/{scope.PublicId}",
            new UpdateScopeCommand { Name = UniqueName() });

        // Then
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenNoToken_WhenPutScope_ThenUnauthorized()
    {
        // Given a scope but no Authorize call (no bearer token on the gateway)
        var scope = await Seeds.ScopeAsync(db, new ScopeOptions { Name = UniqueName(), Description = "Original" });

        // When
        var response = await Gateway.PutAsync<DataOutput<UpdateScopeCommandOutput?>>(
            $"/api/scopes/{scope.PublicId}",
            new UpdateScopeCommand { Name = UniqueName() });

        // Then
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
