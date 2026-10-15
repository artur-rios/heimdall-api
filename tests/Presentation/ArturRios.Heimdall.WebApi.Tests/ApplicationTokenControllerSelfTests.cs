using System.Net;
using System.Net.Http.Headers;
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

// Functional tests for GET /api/applications/self (UC-57) and the isolation between application and
// person tokens (TH-35): the full flow (issue a secret, exchange it, re-check), revocation, credential
// expiry and deletion taking effect on tokens already issued, a person token refused here, an
// application token refused on a person endpoint, and the header forms.
[Collection(nameof(FunctionalCollection))]
public class ApplicationTokenControllerSelfTests(PostgresFixture db) : WebApiTest<Program>(EnvironmentType.Local)
{
    private const string SelfRoute = "/api/applications/self";

    private async Task<string> TokenForAsync(Scope scope, Domain.Entities.Application application, string secret)
    {
        var response = await Gateway.PostAsync<DataOutput<IssueApplicationTokenCommandOutput?>>(
            "/api/applications/token",
            new IssueApplicationTokenCommand { ApplicationId = application.PublicId, ScopeId = scope.PublicId, Secret = secret });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return response.Body!.Data!.Token;
    }

    private Task<HttpOutput<DataOutput<ApplicationTokenCheckOutput?>?>> SelfAsync(string token)
    {
        Authorize(token);
        return Gateway.GetAsync<DataOutput<ApplicationTokenCheckOutput?>>(SelfRoute);
    }

    [FunctionalFact]
    public async Task GivenAnIssuedSecret_WhenExchangedAndChecked_ThenTheApplicationIsReturned()
    {
        // Given — the secret comes from UC-53 itself, not from a seed
        var (scope, owner, application) = await SeedOwnedApplicationAsync(db);
        Authorize(TestTokens.For(owner.PublicId, (int)Roles.ScopeAdmin, null, scope.PublicId));
        var issued = await Gateway.PostAsync<DataOutput<IssueApplicationCredentialCommandOutput?>>(
            Route(scope.PublicId, application.PublicId), new IssueApplicationCredentialCommand());
        var token = await TokenForAsync(scope, application, issued.Body!.Data!.Secret);

        // When
        var response = await SelfAsync(token);

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(application.PublicId, response.Body!.Data!.ApplicationId);
        Assert.Equal(scope.PublicId, response.Body.Data.ScopeId);
        Assert.Equal(issued.Body.Data.Id, response.Body.Data.CredentialId);
        Assert.Equal(application.Name, response.Body.Data.Name);
    }

    [FunctionalFact]
    public async Task GivenOneOfTwoCredentialsRevoked_WhenCheckingTokensOfBoth_ThenOnlyTheOtherStands()
    {
        // Given — tokens issued from both, then one credential revoked through UC-55
        var (scope, _, application) = await SeedOwnedApplicationAsync(db);
        var (first, firstSecret) = await SeedCredentialAsync(db, application);
        var (_, secondSecret) = await SeedCredentialAsync(db, application);
        var firstToken = await TokenForAsync(scope, application, firstSecret);
        var secondToken = await TokenForAsync(scope, application, secondSecret);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));
        var revoke = await Gateway.DeleteAsync<DataOutput<RevokeApplicationCredentialCommandOutput?>>(
            $"{Route(scope.PublicId, application.PublicId)}/{first.PublicId}");
        Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);

        // When
        var revokedCheck = await SelfAsync(firstToken);
        var otherCheck = await SelfAsync(secondToken);

        // Then
        Assert.Equal(HttpStatusCode.Unauthorized, revokedCheck.StatusCode);
        Assert.Equal([ApplicationTokenMessages.TokenNotAccepted], revokedCheck.Body!.Errors);
        Assert.Equal(HttpStatusCode.OK, otherCheck.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenItsCredentialExpiredAfterIssue_WhenCheckingSelf_ThenUnauthorized()
    {
        // Given — the token still has minutes left, the credential has none
        var (scope, _, application) = await SeedOwnedApplicationAsync(db);
        var (credential, secret) = await SeedCredentialAsync(db, application);
        var token = await TokenForAsync(scope, application, secret);
        await using (var context = db.CreateContext())
        {
            await context.ApplicationCredentials.Where(x => x.Id == credential.Id)
                .ExecuteUpdateAsync(set => set.SetProperty(x => x.ExpiresAt, DateTime.UtcNow.AddSeconds(-1)));
        }

        // When
        var response = await SelfAsync(token);

        // Then
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenTheApplicationThenItsScopeDeleted_WhenCheckingSelf_ThenUnauthorized()
    {
        // Given — two applications; one logically deleted, the other's scope logically deleted
        var (scope, _, application) = await SeedOwnedApplicationAsync(db);
        var (_, secret) = await SeedCredentialAsync(db, application);
        var token = await TokenForAsync(scope, application, secret);
        var (otherScope, _, other) = await SeedOwnedApplicationAsync(db);
        var (_, otherSecret) = await SeedCredentialAsync(db, other);
        var otherToken = await TokenForAsync(otherScope, other, otherSecret);
        await using (var context = db.CreateContext())
        {
            await context.Applications.Where(x => x.Id == application.Id)
                .ExecuteUpdateAsync(set => set.SetProperty(x => x.IsDeleted, true));
            await context.Scopes.Where(x => x.Id == otherScope.Id)
                .ExecuteUpdateAsync(set => set.SetProperty(x => x.IsDeleted, true));
        }

        // When
        var deletedApplication = await SelfAsync(token);
        var deletedScope = await SelfAsync(otherToken);

        // Then
        Assert.Equal(HttpStatusCode.Unauthorized, deletedApplication.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, deletedScope.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenAPersonToken_WhenCheckingSelf_ThenUnauthorized()
    {
        // Given — a valid System Admin token, signed with the same key
        var token = TestTokens.ForRole((int)Roles.SystemAdmin);

        // When
        var response = await SelfAsync(token);

        // Then
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal([ApplicationTokenMessages.TokenNotAccepted], response.Body!.Errors);
    }

    [FunctionalFact]
    public async Task GivenAnApplicationToken_WhenCallingAPersonEndpoint_ThenUnauthorized()
    {
        // Given — TH-35: the middleware checks no audience; the mapper's tokenKind refusal holds
        var (scope, _, application) = await SeedOwnedApplicationAsync(db);
        var (_, secret) = await SeedCredentialAsync(db, application);
        var token = await TokenForAsync(scope, application, secret);
        Authorize(token);

        // When
        var response = await Gateway.Client.GetAsync(
            $"/api/persons/{PostgresFixture.StandInPersonIds[Roles.SystemAdmin]}");

        // Then
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenALowercaseBearerScheme_WhenCheckingSelf_ThenOk()
    {
        // Given
        var (scope, _, application) = await SeedOwnedApplicationAsync(db);
        var (_, secret) = await SeedCredentialAsync(db, application);
        var token = await TokenForAsync(scope, application, secret);
        using var request = new HttpRequestMessage(HttpMethod.Get, SelfRoute);
        request.Headers.TryAddWithoutValidation("Authorization", $"bearer {token}");

        // When
        var response = await Gateway.Client.SendAsync(request);

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenNoAuthorizationHeader_WhenCheckingSelf_ThenUnauthorized()
    {
        // Given / When
        var response = await Gateway.GetAsync<DataOutput<ApplicationTokenCheckOutput?>>(SelfRoute);

        // Then
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal([ApplicationTokenMessages.TokenNotAccepted], response.Body!.Errors);
    }
}
