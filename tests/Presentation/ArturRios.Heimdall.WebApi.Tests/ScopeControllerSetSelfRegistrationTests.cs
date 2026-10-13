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

// Functional tests for PUT /api/scopes/{id}/self-registration (UC-47 toggle, FR-SR-02). The rules are
// the handler's and are unit-tested there; these pin the route, the role requirement and persistence.
[Collection(nameof(FunctionalCollection))]
public class ScopeControllerSetSelfRegistrationTests(PostgresFixture db) : WebApiTest<Program>(EnvironmentType.Local)
{
    private static string Route(Guid scopeId) => $"/api/scopes/{scopeId}/self-registration";

    private Task<HttpOutput<DataOutput<SetSelfRegistrationCommandOutput?>?>> SetAsync(Guid scopeId, bool enabled) =>
        Gateway.PutAsync<DataOutput<SetSelfRegistrationCommandOutput?>>(Route(scopeId), new { enabled });

    private async Task<Scope> SeedScopeAsync()
    {
        await using var context = db.CreateContext();
        var scope = new Scope { PublicId = Guid.NewGuid(), Name = $"scope-{Guid.NewGuid():N}" };
        context.Scopes.Add(scope);
        await context.SaveChangesAsync();
        return scope;
    }

    private async Task<bool> PersistedFlagAsync(Scope scope)
    {
        await using var context = db.CreateContext();
        var persisted = await context.Scopes.AsNoTracking().FirstAsync(x => x.PublicId == scope.PublicId);
        return persisted.SelfRegistrationEnabled;
    }

    [FunctionalFact]
    public async Task GivenANewScope_WhenReadingItsFlag_ThenSelfRegistrationIsClosed()
    {
        var scope = await SeedScopeAsync();

        Assert.False(await PersistedFlagAsync(scope));
    }

    [FunctionalFact]
    public async Task GivenSystemAdmin_WhenPutSelfRegistrationEnabled_ThenOkAndFlagIsOn()
    {
        var scope = await SeedScopeAsync();
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        var response = await SetAsync(scope.PublicId, enabled: true);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Body!.Data!.SelfRegistrationEnabled);
        Assert.Contains(ScopeMessages.SelfRegistrationUpdatedSuccessfully, response.Body.Messages);
        Assert.True(await PersistedFlagAsync(scope));
    }

    [FunctionalFact]
    public async Task GivenAUser_WhenPutSelfRegistration_ThenForbiddenAndFlagUnchanged()
    {
        var scope = await SeedScopeAsync();
        Authorize(TestTokens.ForRole((int)Roles.User));

        var response = await SetAsync(scope.PublicId, enabled: true);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(await PersistedFlagAsync(scope));
    }
}
