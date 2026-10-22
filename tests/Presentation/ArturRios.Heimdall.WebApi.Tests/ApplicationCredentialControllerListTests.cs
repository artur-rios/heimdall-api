using System.Net;
using ArturRios.Configuration.Enums;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Query.Output;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Heimdall.WebApi.Tests.Support;
using ArturRios.Output;
using ArturRios.Util.Http;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Functional;
using Microsoft.EntityFrameworkCore;
using static ArturRios.Heimdall.WebApi.Tests.Support.ApplicationSeeds;

namespace ArturRios.Heimdall.WebApi.Tests;

// Functional tests for GET /api/scopes/{scopeId}/applications/{id}/credentials (UC-54): the main flow
// for a System Admin and for the owning Scope Admin (newest first, with liveness, never a secret or a
// hash), the empty list, AF-54a (no live application in the scope), AF-54b (a Scope Admin who does
// not own it), and the framework flows (403 for a User, 401 unauthenticated).
[Collection(nameof(FunctionalCollection))]
public class ApplicationCredentialControllerListTests(PostgresFixture db) : WebApiTest<Program>(EnvironmentType.Local)
{
    private Task<HttpOutput<DataOutput<ApplicationCredentialListOutput?>?>> ListAsync(
        Scope scope, Domain.Entities.Application application) =>
        Gateway.GetAsync<DataOutput<ApplicationCredentialListOutput?>>(Route(scope.PublicId, application.PublicId));

    [FunctionalFact]
    public async Task GivenCredentials_WhenListingAsSystemAdmin_ThenNewestFirstWithLivenessAndNoSecret()
    {
        // Given — a revoked credential, then a live one
        var (scope, _, application) = await SeedOwnedApplicationAsync(db);
        var (revoked, revokedSecret) = await SeedCredentialAsync(
            db, application, revokedAt: DateTime.UtcNow.AddDays(-1), createdAt: DateTime.UtcNow.AddMinutes(-1));
        var (live, liveSecret) = await SeedCredentialAsync(db, application);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await ListAsync(scope, application);
        var raw = await Gateway.Client.GetStringAsync(Route(scope.PublicId, application.PublicId));

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(ApplicationCredentialMessages.CredentialsRetrieved, response.Body!.Messages.Single());
        var listed = response.Body.Data!.Credentials;
        Assert.Equal([live.PublicId, revoked.PublicId], listed.Select(c => c.Id));
        Assert.Equal([true, false], listed.Select(c => c.Live));
        Assert.DoesNotContain(liveSecret, raw);
        Assert.DoesNotContain(revokedSecret, raw);
        Assert.DoesNotContain("secretHash", raw, StringComparison.OrdinalIgnoreCase);
    }

    [FunctionalFact]
    public async Task GivenTheOwningScopeAdmin_WhenListing_ThenOk()
    {
        // Given
        var (scope, owner, application) = await SeedOwnedApplicationAsync(db);
        await SeedCredentialAsync(db, application);
        Authorize(TestTokens.For(owner.PublicId, (int)Roles.ScopeAdmin, null, scope.PublicId));

        // When
        var response = await ListAsync(scope, application);

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(response.Body!.Data!.Credentials);
    }

    [FunctionalFact]
    public async Task GivenNoCredentials_WhenListing_ThenOkWithAnEmptyList()
    {
        // Given
        var (scope, _, application) = await SeedOwnedApplicationAsync(db);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await ListAsync(scope, application);

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(response.Body!.Data!.Credentials);
    }

    [FunctionalFact]
    public async Task GivenAnApplicationInAnotherScope_WhenListing_ThenNotFound()
    {
        // Given
        var (_, _, application) = await SeedOwnedApplicationAsync(db);
        var otherScope = await Seeds.ScopeAsync(db);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await ListAsync(otherScope, application);

        // Then
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(ApplicationMessages.ApplicationNotFound, response.Body!.Errors.Single());
    }

    [FunctionalFact]
    public async Task GivenACoOwnerOfTheScopeWhoDoesNotOwnTheApplication_WhenListing_ThenForbidden()
    {
        // Given
        var (scope, _, application) = await SeedOwnedApplicationAsync(db);
        var coOwner = await Seeds.PersonAsync(db, Roles.ScopeAdmin, new PersonOptions { Name = "Admin", EmailPrefix = "admin", Owns = [scope] });
        Authorize(TestTokens.For(coOwner.PublicId, (int)Roles.ScopeAdmin, null, scope.PublicId));

        // When
        var response = await ListAsync(scope, application);

        // Then
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(ApplicationCredentialMessages.NotAuthorizedToManageCredentials, response.Body!.Errors.Single());
    }

    [FunctionalFact]
    public async Task GivenAUser_WhenListing_ThenForbidden()
    {
        // Given
        var (scope, _, application) = await SeedOwnedApplicationAsync(db);
        Authorize(TestTokens.ForRole((int)Roles.User));

        // When
        var response = await ListAsync(scope, application);

        // Then
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenNoToken_WhenListing_ThenUnauthorized()
    {
        // Given
        var (scope, _, application) = await SeedOwnedApplicationAsync(db);

        // When
        var response = await ListAsync(scope, application);

        // Then
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
