using System.Net;
using System.Text.Json;
using ArturRios.Configuration.Enums;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Command.Services;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Heimdall.WebApi.Tests.Support;
using ArturRios.Output;
using ArturRios.Util.Http;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Functional;
using Microsoft.EntityFrameworkCore;
using static ArturRios.Heimdall.WebApi.Tests.Support.ApplicationSeeds;

namespace ArturRios.Heimdall.WebApi.Tests;

// Functional tests for POST /api/scopes/{scopeId}/applications/{id}/credentials (UC-53): the main flow
// for a System Admin and for the owning Scope Admin, an optional or offset-less expiry, AF-53a (no live
// application in the scope), AF-53b (a Scope Admin who does not own it), AF-53c (an expiry in the past,
// one that is not a date-time, or a body that is not JSON), AF-53d (two live already, including under concurrency), the audit entry, and the framework flows
// (403 for a User, 401 unauthenticated).
[Collection(nameof(FunctionalCollection))]
public class ApplicationCredentialControllerIssueTests(PostgresFixture db) : WebApiTest<Program>(EnvironmentType.Local)
{
    private Task<HttpOutput<DataOutput<IssueApplicationCredentialCommandOutput?>?>> IssueAsync(
        Scope scope, Domain.Entities.Application application, IssueApplicationCredentialCommand? body = null) =>
        Gateway.PostAsync<DataOutput<IssueApplicationCredentialCommandOutput?>>(
            Route(scope.PublicId, application.PublicId), body ?? new IssueApplicationCredentialCommand());

    [FunctionalFact]
    public async Task GivenASystemAdmin_WhenIssuing_ThenCreatedWithTheSecretOnceAndOnlyItsHashStored()
    {
        // Given
        var (scope, _, application) = await SeedOwnedApplicationAsync(db);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await IssueAsync(scope, application);

        // Then
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var data = response.Body!.Data!;
        Assert.StartsWith(ApplicationCredentialSecret.Prefix, data.Secret);
        var stored = Assert.Single(await CredentialsOfAsync(db, application));
        Assert.Equal(data.Id, stored.PublicId);
        Assert.Equal(ApplicationCredentialSecret.HashOf(data.Secret), stored.SecretHash);
        Assert.Null(stored.ExpiresAt);
        Assert.Null(stored.RevokedAt);
    }

    [FunctionalFact]
    public async Task GivenTheOwningScopeAdmin_WhenIssuing_ThenCreated()
    {
        // Given
        var (scope, owner, application) = await SeedOwnedApplicationAsync(db);
        Authorize(TestTokens.For(owner.PublicId, (int)Roles.ScopeAdmin, null, scope.PublicId));

        // When
        var response = await IssueAsync(scope, application);

        // Then
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenNoBodyAtAll_WhenIssuing_ThenCreatedWithoutExpiry()
    {
        // Given — a JSON POST with nothing in it: the body is optional (EmptyBodyBehavior.Allow)
        var (scope, _, application) = await SeedOwnedApplicationAsync(db);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));
        using var empty = new StringContent(string.Empty, System.Text.Encoding.UTF8, "application/json");

        // When
        var response = await Gateway.Client.PostAsync(Route(scope.PublicId, application.PublicId), empty);

        // Then
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Null(Assert.Single(await CredentialsOfAsync(db, application)).ExpiresAt);
    }

    [FunctionalFact]
    public async Task GivenAnExpiryWithoutAnOffset_WhenIssuing_ThenCreatedAndStoredAsUtc()
    {
        // Given — what a datetime-local input sends
        var (scope, _, application) = await SeedOwnedApplicationAsync(db);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));
        using var body = new StringContent(
            "{\"expiresAt\":\"2030-01-01T00:00:00\"}", System.Text.Encoding.UTF8, "application/json");

        // When
        var response = await Gateway.Client.PostAsync(Route(scope.PublicId, application.PublicId), body);

        // Then
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var stored = Assert.Single(await CredentialsOfAsync(db, application));
        Assert.Equal(new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc), stored.ExpiresAt!.Value.ToUniversalTime());
    }

    [FunctionalFact]
    public async Task GivenAPastExpiry_WhenIssuing_ThenBadRequest()
    {
        // Given
        var (scope, _, application) = await SeedOwnedApplicationAsync(db);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await IssueAsync(scope, application,
            new IssueApplicationCredentialCommand { ExpiresAt = DateTime.UtcNow.AddMinutes(-5) });

        // Then
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ApplicationCredentialMessages.ExpiryNotInFuture, response.Body!.Errors.Single());
        Assert.Empty(await CredentialsOfAsync(db, application));
    }

    /// <summary>Posts <paramref name="json" /> as it is, and reads the answer's status and errors.</summary>
    private async Task<(HttpStatusCode Status, string[] Errors)> PostRawAsync(
        Scope scope, Domain.Entities.Application application, string json)
    {
        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        using var response = await Gateway.Client.PostAsync(Route(scope.PublicId, application.PublicId), content);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var errors = document.RootElement.GetProperty("errors").EnumerateArray()
            .Select(error => error.GetString()!)
            .ToArray();
        return (response.StatusCode, errors);
    }

    [FunctionalTheory]
    [InlineData("""{"expiresAt":"garbage"}""")]
    [InlineData("""{"expiresAt":1893456000}""")]
    public async Task GivenAnExpiryThatIsNotADateTime_WhenIssuing_ThenBadRequestAndNothingStored(string json)
    {
        // Given — an expiry that cannot be read must not become "never expires"
        var (scope, _, application) = await SeedOwnedApplicationAsync(db);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var (status, errors) = await PostRawAsync(scope, application, json);

        // Then
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal([RequestMessages.Malformed], errors);
        Assert.Empty(await CredentialsOfAsync(db, application));
    }

    [FunctionalFact]
    public async Task GivenABodyThatIsNotJson_WhenIssuing_ThenBadRequestAndNothingStored()
    {
        // Given
        var (scope, _, application) = await SeedOwnedApplicationAsync(db);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var (status, errors) = await PostRawAsync(scope, application, "{\"expiresAt\":");

        // Then
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal([RequestMessages.Malformed], errors);
        Assert.Empty(await CredentialsOfAsync(db, application));
    }

    [FunctionalFact]
    public async Task GivenAnApplicationInAnotherScope_WhenIssuing_ThenNotFound()
    {
        // Given
        var (_, _, application) = await SeedOwnedApplicationAsync(db);
        var otherScope = await SeedScopeAsync(db);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await IssueAsync(otherScope, application);

        // Then
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(ApplicationMessages.ApplicationNotFound, response.Body!.Errors.Single());
    }

    [FunctionalFact]
    public async Task GivenALogicallyDeletedApplication_WhenIssuing_ThenNotFound()
    {
        // Given
        var (scope, _, application) = await SeedOwnedApplicationAsync(db);
        await using (var context = db.CreateContext())
        {
            await context.Applications.Where(x => x.Id == application.Id)
                .ExecuteUpdateAsync(set => set.SetProperty(x => x.IsDeleted, true));
        }
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await IssueAsync(scope, application);

        // Then
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenACoOwnerOfTheScopeWhoDoesNotOwnTheApplication_WhenIssuing_ThenForbidden()
    {
        // Given
        var (scope, _, application) = await SeedOwnedApplicationAsync(db);
        var coOwner = await SeedScopeAdminAsync(db, scope);
        Authorize(TestTokens.For(coOwner.PublicId, (int)Roles.ScopeAdmin, null, scope.PublicId));

        // When
        var response = await IssueAsync(scope, application);

        // Then
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(ApplicationCredentialMessages.NotAuthorizedToManageCredentials, response.Body!.Errors.Single());
        Assert.Empty(await CredentialsOfAsync(db, application));
    }

    [FunctionalFact]
    public async Task GivenTwoLiveCredentials_WhenIssuingAThird_ThenConflict()
    {
        // Given — plus a revoked one, which does not count
        var (scope, _, application) = await SeedOwnedApplicationAsync(db);
        await SeedCredentialAsync(db, application);
        await SeedCredentialAsync(db, application);
        await SeedCredentialAsync(db, application, revokedAt: DateTime.UtcNow.AddDays(-1));
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await IssueAsync(scope, application);

        // Then
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(ApplicationCredentialMessages.CredentialLimitReached, response.Body!.Errors.Single());
        Assert.Equal(3, (await CredentialsOfAsync(db, application)).Count);
    }

    [FunctionalFact]
    public async Task GivenNoCredentials_WhenIssuingInParallel_ThenExactlyTwoAreLive()
    {
        // Given
        var (scope, _, application) = await SeedOwnedApplicationAsync(db);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // When — eight requests released at the same moment
        var requests = Enumerable.Range(0, 8)
            .Select(_ => Task.Run(async () =>
            {
                await start.Task;
                return await IssueAsync(scope, application);
            }))
            .ToArray();
        start.SetResult();
        var responses = await Task.WhenAll(requests);

        // Then
        Assert.Equal(2, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(6, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        Assert.Equal(2, (await CredentialsOfAsync(db, application)).Count);
    }

    [FunctionalFact]
    public async Task GivenAnIssuedCredential_WhenReadingTheAuditTrail_ThenTheCredentialIsTheTarget()
    {
        // Given
        var (scope, owner, application) = await SeedOwnedApplicationAsync(db);
        Authorize(TestTokens.For(owner.PublicId, (int)Roles.ScopeAdmin, null, scope.PublicId));

        // When
        var response = await IssueAsync(scope, application);

        // Then — the secret is in no audit column
        await using var context = db.CreateContext();
        var entry = await context.AuditLogs.AsNoTracking()
            .SingleAsync(a => a.Action == nameof(IssueApplicationCredentialCommand) && a.TargetId == response.Body!.Data!.Id);
        Assert.True(entry.Succeeded);
        Assert.Equal(owner.PublicId, entry.ActorPersonId);
    }

    [FunctionalFact]
    public async Task GivenAUser_WhenIssuing_ThenForbidden()
    {
        // Given
        var (scope, _, application) = await SeedOwnedApplicationAsync(db);
        Authorize(TestTokens.ForRole((int)Roles.User));

        // When
        var response = await IssueAsync(scope, application);

        // Then
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenNoToken_WhenIssuing_ThenUnauthorized()
    {
        // Given
        var (scope, _, application) = await SeedOwnedApplicationAsync(db);

        // When
        var response = await IssueAsync(scope, application);

        // Then
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
