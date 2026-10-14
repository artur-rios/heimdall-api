using System.Net;
using ArturRios.Configuration.Enums;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Heimdall.Shared.Security;
using ArturRios.Heimdall.WebApi.Tests.Support;
using ArturRios.Output;
using ArturRios.Util.Http;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Functional;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.JsonWebTokens;
using static ArturRios.Heimdall.WebApi.Tests.Support.ApplicationSeeds;

namespace ArturRios.Heimdall.WebApi.Tests;

// Functional tests for POST /api/applications/token (UC-56): the main flow (a token for the
// application, under the application audience, LastUsedAt stamped, audited with no actor), a secret
// with surrounding whitespace, AF-56a (malformed request) and AF-56b — every refusal identical,
// including scope isolation (a credential of scope A cannot obtain a token naming scope B).
[Collection(nameof(FunctionalCollection))]
public class ApplicationTokenControllerIssueTests(PostgresFixture db) : WebApiTest<Program>(EnvironmentType.Local)
{
    private const string Route = "/api/applications/token";

    private Task<HttpOutput<DataOutput<IssueApplicationTokenCommandOutput?>?>> ExchangeAsync(
        Guid applicationId, Guid scopeId, string secret) =>
        Gateway.PostAsync<DataOutput<IssueApplicationTokenCommandOutput?>>(Route,
            new IssueApplicationTokenCommand { ApplicationId = applicationId, ScopeId = scopeId, Secret = secret });

    [FunctionalFact]
    public async Task GivenALiveCredential_WhenExchanging_ThenOkWithAnApplicationToken()
    {
        // Given
        var (scope, _, application) = await SeedOwnedApplicationAsync(db);
        var (credential, secret) = await SeedCredentialAsync(db, application);

        // When
        var response = await ExchangeAsync(application.PublicId, scope.PublicId, secret);

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var token = new JsonWebToken(response.Body!.Data!.Token);
        Assert.Equal([PostgresFixture.ApplicationTokenAudience], token.Audiences);
        Assert.Equal(ApplicationTokenClaims.ApplicationKind, token.GetClaim(ApplicationTokenClaims.TokenKind).Value);
        Assert.Equal(application.PublicId.ToString(), token.GetClaim(ApplicationTokenClaims.Id).Value);
        Assert.Equal(scope.PublicId.ToString(), token.GetClaim(ApplicationTokenClaims.ScopeId).Value);
        Assert.Equal(credential.PublicId.ToString(), token.GetClaim(ApplicationTokenClaims.CredentialId).Value);
        Assert.NotNull(Assert.Single(await CredentialsOfAsync(db, application)).LastUsedAt);
    }

    [FunctionalFact]
    public async Task GivenAnExchange_WhenReadingTheAuditTrail_ThenTheApplicationIsTheTargetWithNoActor()
    {
        // Given
        var (scope, _, application) = await SeedOwnedApplicationAsync(db);
        var (_, secret) = await SeedCredentialAsync(db, application);

        // When
        await ExchangeAsync(application.PublicId, scope.PublicId, secret);

        // Then
        await using var context = db.CreateContext();
        var entry = await context.AuditLogs.AsNoTracking()
            .SingleAsync(a => a.Action == nameof(IssueApplicationTokenCommand) && a.TargetId == application.PublicId);
        Assert.True(entry.Succeeded);
        Assert.Null(entry.ActorPersonId);
    }

    [FunctionalFact]
    public async Task GivenASecretWithATrailingNewline_WhenExchanging_ThenOk()
    {
        // Given
        var (scope, _, application) = await SeedOwnedApplicationAsync(db);
        var (_, secret) = await SeedCredentialAsync(db, application);

        // When
        var response = await ExchangeAsync(application.PublicId, scope.PublicId, secret + "\n");

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenEveryKindOfRefusal_WhenExchanging_ThenEachIsTheSameUnauthorizedAnswer()
    {
        // Given — seven ways to be wrong, compared with the timestamps normalised
        var (scope, _, application) = await SeedOwnedApplicationAsync(db);
        var (_, secret) = await SeedCredentialAsync(db, application);
        var (_, revoked) = await SeedCredentialAsync(db, application, revokedAt: DateTime.UtcNow.AddMinutes(-1));
        var (otherScope, _, otherApplication) = await SeedOwnedApplicationAsync(db);
        var (_, expired) = await SeedCredentialAsync(db, otherApplication, expiresAt: DateTime.UtcNow.AddMinutes(-1));
        var (_, otherSecret) = await SeedCredentialAsync(db, otherApplication);

        // When
        var responses = new[]
        {
            await ExchangeAsync(application.PublicId, scope.PublicId, "hds_" + new string('x', 43)),
            await ExchangeAsync(application.PublicId, scope.PublicId, revoked),
            await ExchangeAsync(otherApplication.PublicId, otherScope.PublicId, expired),
            await ExchangeAsync(application.PublicId, scope.PublicId, otherSecret),      // another application's
            await ExchangeAsync(Guid.NewGuid(), scope.PublicId, secret),                 // unknown application
            await ExchangeAsync(application.PublicId, otherScope.PublicId, secret)       // scope isolation
        };

        // Then
        Assert.All(responses, response =>
        {
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal([ApplicationTokenMessages.CredentialsNotAccepted], response.Body!.Errors);
            Assert.Null(response.Body.Data);
        });
    }

    [FunctionalFact]
    public async Task GivenADeletedApplicationOrScope_WhenExchanging_ThenUnauthorized()
    {
        // Given — one application logically deleted, another whose scope is
        var (scope, _, application) = await SeedOwnedApplicationAsync(db);
        var (_, secret) = await SeedCredentialAsync(db, application);
        var (deletedScope, _, inDeletedScope) = await SeedOwnedApplicationAsync(db);
        var (_, secretInDeletedScope) = await SeedCredentialAsync(db, inDeletedScope);
        await using (var context = db.CreateContext())
        {
            await context.Applications.Where(x => x.Id == application.Id)
                .ExecuteUpdateAsync(set => set.SetProperty(x => x.IsDeleted, true));
            await context.Scopes.Where(x => x.Id == deletedScope.Id)
                .ExecuteUpdateAsync(set => set.SetProperty(x => x.IsDeleted, true));
        }

        // When
        var first = await ExchangeAsync(application.PublicId, scope.PublicId, secret);
        var second = await ExchangeAsync(inDeletedScope.PublicId, deletedScope.PublicId, secretInDeletedScope);

        // Then
        Assert.Equal(HttpStatusCode.Unauthorized, first.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, second.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenARefusal_WhenReadingTheAuditTrail_ThenItIsRecordedWithoutATarget()
    {
        // Given
        var (scope, _, application) = await SeedOwnedApplicationAsync(db);
        var before = DateTime.UtcNow.AddSeconds(-1);

        // When
        await ExchangeAsync(application.PublicId, scope.PublicId, "hds_" + new string('y', 43));

        // Then — the canonical reason, never the submitted secret
        await using var context = db.CreateContext();
        var entry = await context.AuditLogs.AsNoTracking()
            .Where(a => a.Action == nameof(IssueApplicationTokenCommand) && !a.Succeeded && a.CreatedAt >= before)
            .OrderByDescending(a => a.Id)
            .FirstAsync();
        Assert.Null(entry.TargetId);
        Assert.Equal(ApplicationTokenMessages.CredentialsNotAccepted, entry.FailureReason);
    }

    [FunctionalFact]
    public async Task GivenAMissingSecret_WhenExchanging_ThenBadRequest()
    {
        // Given
        var (scope, _, application) = await SeedOwnedApplicationAsync(db);

        // When
        var response = await ExchangeAsync(application.PublicId, scope.PublicId, string.Empty);

        // Then
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal([ApplicationTokenMessages.SecretRequired], response.Body!.Errors);
    }
}
