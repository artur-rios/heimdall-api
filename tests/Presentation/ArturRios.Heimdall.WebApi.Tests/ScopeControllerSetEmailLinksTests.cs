using System.Net;
using ArturRios.Configuration.Enums;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Heimdall.WebApi.Tests.Support;
using ArturRios.Output;
using ArturRios.Util.Http;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Functional;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Heimdall.WebApi.Tests;

// Functional tests for PUT /api/scopes/{id}/email-links. The rules are the handler's and the
// validator's and are unit-tested there; these pin the route, the role requirement and persistence.
[Collection(nameof(FunctionalCollection))]
public class ScopeControllerSetEmailLinksTests(PostgresFixture db) : WebApiTest<Program>(EnvironmentType.Local)
{
    private const string VerifyUrl = "https://fortuna.test/verify-email";
    private const string ResetUrl = "https://fortuna.test/password-reset";

    private static string Route(Guid scopeId) => $"/api/scopes/{scopeId}/email-links";

    private Task<HttpOutput<DataOutput<SetScopeEmailLinksCommandOutput?>?>> SetAsync(
        Guid scopeId, string? verificationUrl, string? passwordResetUrl) =>
        Gateway.PutAsync<DataOutput<SetScopeEmailLinksCommandOutput?>>(
            Route(scopeId), new { emailVerificationUrl = verificationUrl, passwordResetUrl });

    private async Task<Scope> SeedScopeAsync()
    {
        await using var context = db.CreateContext();
        var scope = new Scope { PublicId = Guid.NewGuid(), Name = $"scope-{Guid.NewGuid():N}" };
        context.Scopes.Add(scope);
        await context.SaveChangesAsync();
        return scope;
    }

    private async Task<Scope> PersistedAsync(Scope scope)
    {
        await using var context = db.CreateContext();
        return await context.Scopes.AsNoTracking().FirstAsync(x => x.PublicId == scope.PublicId);
    }

    [FunctionalFact]
    public async Task GivenSystemAdmin_WhenPutEmailLinks_ThenOkAndBothLinksAreStored()
    {
        var scope = await SeedScopeAsync();
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        var response = await SetAsync(scope.PublicId, VerifyUrl, ResetUrl);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(VerifyUrl, response.Body!.Data!.EmailVerificationUrl);
        Assert.Equal(ResetUrl, response.Body.Data.PasswordResetUrl);
        Assert.Contains(ScopeMessages.EmailLinksUpdatedSuccessfully, response.Body.Messages);

        var persisted = await PersistedAsync(scope);
        Assert.Equal(VerifyUrl, persisted.EmailVerificationUrl);
        Assert.Equal(ResetUrl, persisted.PasswordResetUrl);
    }

    [FunctionalFact]
    public async Task GivenSystemAdmin_WhenPutNullLinks_ThenTheStoredLinksAreCleared()
    {
        var scope = await SeedScopeAsync();
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));
        await SetAsync(scope.PublicId, VerifyUrl, ResetUrl);

        var response = await SetAsync(scope.PublicId, null, null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var persisted = await PersistedAsync(scope);
        Assert.Null(persisted.EmailVerificationUrl);
        Assert.Null(persisted.PasswordResetUrl);
    }

    [FunctionalFact]
    public async Task GivenARelativeLink_WhenPutEmailLinks_ThenBadRequestAndNothingIsStored()
    {
        var scope = await SeedScopeAsync();
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        var response = await SetAsync(scope.PublicId, "/verify-email", ResetUrl);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(ScopeMessages.EmailLinkInvalid, response.Body!.Errors);
        Assert.Null((await PersistedAsync(scope)).EmailVerificationUrl);
    }

    [FunctionalFact]
    public async Task GivenAUser_WhenPutEmailLinks_ThenForbiddenAndNothingIsStored()
    {
        var scope = await SeedScopeAsync();
        Authorize(TestTokens.ForRole((int)Roles.User));

        var response = await SetAsync(scope.PublicId, VerifyUrl, ResetUrl);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var persisted = await PersistedAsync(scope);
        Assert.Null(persisted.EmailVerificationUrl);
        Assert.Null(persisted.PasswordResetUrl);
    }

    [FunctionalFact]
    public async Task GivenAnUnknownScope_WhenPutEmailLinks_ThenNotFound()
    {
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        var response = await SetAsync(Guid.NewGuid(), VerifyUrl, ResetUrl);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
