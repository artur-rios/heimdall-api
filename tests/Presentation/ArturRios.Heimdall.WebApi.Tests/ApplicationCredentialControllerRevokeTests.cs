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

// Functional tests for DELETE /api/scopes/{scopeId}/applications/{id}/credentials/{credentialId}
// (UC-55): the main flow, AF-55a (no live application in the scope), AF-55b (a Scope Admin who does
// not own it), AF-55c (no such credential on this application, including another application's),
// AF-55d (already revoked), the audit entry, and the framework flows (403 for a User, 401
// unauthenticated).
[Collection(nameof(FunctionalCollection))]
public class ApplicationCredentialControllerRevokeTests(PostgresFixture db) : WebApiTest<Program>(EnvironmentType.Local)
{
    private Task<HttpOutput<DataOutput<RevokeApplicationCredentialCommandOutput?>?>> RevokeAsync(
        Scope scope, Domain.Entities.Application application, Guid credentialId) =>
        Gateway.DeleteAsync<DataOutput<RevokeApplicationCredentialCommandOutput?>>(
            $"{Route(scope.PublicId, application.PublicId)}/{credentialId}");

    [FunctionalFact]
    public async Task GivenALiveCredential_WhenRevoking_ThenOkAndRevokedAtIsStored()
    {
        // Given
        var (scope, _, application) = await SeedOwnedApplicationAsync(db);
        var (credential, _) = await SeedCredentialAsync(db, application);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await RevokeAsync(scope, application, credential.PublicId);

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(ApplicationCredentialMessages.CredentialRevoked, response.Body!.Messages.Single());
        Assert.Equal(credential.PublicId, response.Body.Data!.Id);
        Assert.NotNull(Assert.Single(await CredentialsOfAsync(db, application)).RevokedAt);
    }

    [FunctionalFact]
    public async Task GivenARevokedCredential_WhenRevokingAgain_ThenConflict()
    {
        // Given
        var (scope, _, application) = await SeedOwnedApplicationAsync(db);
        var (credential, _) = await SeedCredentialAsync(db, application);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));
        await RevokeAsync(scope, application, credential.PublicId);
        var firstStamp = Assert.Single(await CredentialsOfAsync(db, application)).RevokedAt;

        // When
        var response = await RevokeAsync(scope, application, credential.PublicId);

        // Then
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(ApplicationCredentialMessages.CredentialAlreadyRevoked, response.Body!.Errors.Single());
        Assert.Equal(firstStamp, Assert.Single(await CredentialsOfAsync(db, application)).RevokedAt);
    }

    [FunctionalFact]
    public async Task GivenACredentialOfAnotherApplication_WhenRevoking_ThenNotFound()
    {
        // Given — a second owned application with a credential, addressed through the first one's route
        var (scope, owner, application) = await SeedOwnedApplicationAsync(db);
        var other = await SeedApplicationAsync(db, scope, owner);
        var (credential, _) = await SeedCredentialAsync(db, other);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await RevokeAsync(scope, application, credential.PublicId);

        // Then
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(ApplicationCredentialMessages.CredentialNotFound, response.Body!.Errors.Single());
        Assert.Null(Assert.Single(await CredentialsOfAsync(db, other)).RevokedAt);
    }

    [FunctionalFact]
    public async Task GivenAnUnknownCredential_WhenRevoking_ThenNotFound()
    {
        // Given
        var (scope, _, application) = await SeedOwnedApplicationAsync(db);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await RevokeAsync(scope, application, Guid.NewGuid());

        // Then
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(ApplicationCredentialMessages.CredentialNotFound, response.Body!.Errors.Single());
    }

    [FunctionalFact]
    public async Task GivenAnApplicationInAnotherScope_WhenRevoking_ThenNotFound()
    {
        // Given
        var (_, _, application) = await SeedOwnedApplicationAsync(db);
        var (credential, _) = await SeedCredentialAsync(db, application);
        var otherScope = await SeedScopeAsync(db);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await RevokeAsync(otherScope, application, credential.PublicId);

        // Then
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(ApplicationMessages.ApplicationNotFound, response.Body!.Errors.Single());
        Assert.Null(Assert.Single(await CredentialsOfAsync(db, application)).RevokedAt);
    }

    [FunctionalFact]
    public async Task GivenACoOwnerOfTheScopeWhoDoesNotOwnTheApplication_WhenRevoking_ThenForbidden()
    {
        // Given
        var (scope, _, application) = await SeedOwnedApplicationAsync(db);
        var (credential, _) = await SeedCredentialAsync(db, application);
        var coOwner = await SeedScopeAdminAsync(db, scope);
        Authorize(TestTokens.For(coOwner.PublicId, (int)Roles.ScopeAdmin, null, scope.PublicId));

        // When
        var response = await RevokeAsync(scope, application, credential.PublicId);

        // Then
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(ApplicationCredentialMessages.NotAuthorizedToManageCredentials, response.Body!.Errors.Single());
        Assert.Null(Assert.Single(await CredentialsOfAsync(db, application)).RevokedAt);
    }

    [FunctionalFact]
    public async Task GivenARevocation_WhenReadingTheAuditTrail_ThenTheCredentialIsTheTarget()
    {
        // Given
        var (scope, owner, application) = await SeedOwnedApplicationAsync(db);
        var (credential, _) = await SeedCredentialAsync(db, application);
        Authorize(TestTokens.For(owner.PublicId, (int)Roles.ScopeAdmin, null, scope.PublicId));

        // When
        await RevokeAsync(scope, application, credential.PublicId);

        // Then
        await using var context = db.CreateContext();
        var entry = await context.AuditLogs.AsNoTracking()
            .SingleAsync(a => a.Action == nameof(RevokeApplicationCredentialCommand) && a.TargetId == credential.PublicId);
        Assert.True(entry.Succeeded);
        Assert.Equal(owner.PublicId, entry.ActorPersonId);
    }

    [FunctionalFact]
    public async Task GivenAUser_WhenRevoking_ThenForbidden()
    {
        // Given
        var (scope, _, application) = await SeedOwnedApplicationAsync(db);
        var (credential, _) = await SeedCredentialAsync(db, application);
        Authorize(TestTokens.ForRole((int)Roles.User));

        // When
        var response = await RevokeAsync(scope, application, credential.PublicId);

        // Then
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenNoToken_WhenRevoking_ThenUnauthorized()
    {
        // Given
        var (scope, _, application) = await SeedOwnedApplicationAsync(db);
        var (credential, _) = await SeedCredentialAsync(db, application);

        // When
        var response = await RevokeAsync(scope, application, credential.PublicId);

        // Then
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
