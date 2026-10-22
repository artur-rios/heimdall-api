using System.Net;
using System.Text;
using System.Text.Json;
using ArturRios.Configuration.Enums;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Heimdall.WebApi.Tests.Support;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Functional;
using static ArturRios.Heimdall.WebApi.Tests.Support.ApplicationSeeds;

namespace ArturRios.Heimdall.WebApi.Tests;

// Functional tests for the global MalformedRequestFilter (NFR-10, #186): a request MVC could not bind
// — a body that is not JSON, a query value of the wrong type, a body field of the wrong type — is a
// 400 with RequestMessages.Malformed, on every endpoint, once authorization has passed. An empty body
// that an action declares optional (UC-53) is not malformed.
[Collection(nameof(FunctionalCollection))]
public class MalformedRequestTests(PostgresFixture db) : WebApiTest<Program>(EnvironmentType.Local)
{
    private async Task<(HttpStatusCode Status, string[] Errors)> SendAsync(
        HttpMethod method, string route, string? json = null)
    {
        using var request = new HttpRequestMessage(method, route);
        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        using var response = await Gateway.Client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        var errors = text.Length > 0 && text.StartsWith('{')
            ? JsonDocument.Parse(text).RootElement.TryGetProperty("errors", out var array)
                ? array.EnumerateArray().Select(error => error.GetString()!).ToArray()
                : []
            : [];
        return (response.StatusCode, errors);
    }

    [FunctionalFact]
    public async Task GivenABodyThatIsNotJson_WhenLoggingIn_ThenBadRequestWithTheSharedMessage()
    {
        // Given / When
        var (status, errors) = await SendAsync(HttpMethod.Post, "/api/auth/login", "{\"email\":");

        // Then
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal([RequestMessages.Malformed], errors);
    }

    [FunctionalFact]
    public async Task GivenABooleanFilterThatIsNotABoolean_WhenListingTheAuditLog_ThenBadRequest()
    {
        // Given
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var (status, errors) = await SendAsync(HttpMethod.Get, "/api/audit-log?succeeded=maybe");

        // Then
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal([RequestMessages.Malformed], errors);
    }

    [FunctionalFact]
    public async Task GivenAnActorIdThatIsNotAGuid_WhenListingTheAuditLog_ThenBadRequest()
    {
        // Given
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var (status, errors) = await SendAsync(HttpMethod.Get, "/api/audit-log?actorId=not-a-guid");

        // Then
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal([RequestMessages.Malformed], errors);
    }

    [FunctionalFact]
    public async Task GivenAMalformedQueryWithoutAToken_WhenListingTheAuditLog_ThenUnauthorizedNotBadRequest()
    {
        // Given / When — authorization answers before the binding does
        var (status, _) = await SendAsync(HttpMethod.Get, "/api/audit-log?succeeded=maybe");

        // Then
        Assert.Equal(HttpStatusCode.Unauthorized, status);
    }

    [FunctionalFact]
    public async Task GivenAnEmptyBody_WhenIssuingACredential_ThenStillCreated()
    {
        // Given
        var (scope, _, application) = await SeedOwnedApplicationAsync(db);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var (status, _) = await SendAsync(HttpMethod.Post, Route(scope.PublicId, application.PublicId), "");

        // Then
        Assert.Equal(HttpStatusCode.Created, status);
    }

    [FunctionalFact]
    public async Task GivenABodyFieldOfTheWrongType_WhenUpdatingAScope_ThenBadRequest()
    {
        // Given
        var scope = await Seeds.ScopeAsync(db);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var (status, errors) = await SendAsync(HttpMethod.Put, $"/api/scopes/{scope.PublicId}", """{"name":123}""");

        // Then
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal([RequestMessages.Malformed], errors);
    }

    [FunctionalFact]
    public async Task GivenAnEmptyBody_WhenExchangingAnApplicationSecret_ThenBadRequest()
    {
        // Given / When — a body the action requires, so no command binds
        var (status, errors) = await SendAsync(HttpMethod.Post, "/api/applications/token", "");

        // Then
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal([RequestMessages.Malformed], errors);
    }

    [FunctionalFact]
    public async Task GivenANullSecret_WhenExchangingAnApplicationSecret_ThenTheFieldMessageNotTheMalformedOne()
    {
        // Given — null binds fine; required-ness belongs to FluentValidation, not to the binder
        var json = $$"""{"applicationId":"{{Guid.NewGuid()}}","scopeId":"{{Guid.NewGuid()}}","secret":null}""";

        // When
        var (status, errors) = await SendAsync(HttpMethod.Post, "/api/applications/token", json);

        // Then
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal([ApplicationTokenMessages.SecretRequired], errors);
    }

    [FunctionalFact]
    public async Task GivenANullEmail_WhenLoggingIn_ThenTheFieldMessageNotTheMalformedOne()
    {
        // Given / When
        var (status, errors) = await SendAsync(
            HttpMethod.Post, "/api/auth/login", """{"email":null,"password":"x"}""");

        // Then
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.DoesNotContain(RequestMessages.Malformed, errors);
        Assert.NotEmpty(errors);
    }

    [FunctionalFact]
    public async Task GivenAPageNumberThatIsNotANumber_WhenListingScopes_ThenBadRequest()
    {
        // Given
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var (status, errors) = await SendAsync(HttpMethod.Get, "/api/scopes?pageNumber=abc");

        // Then
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal([RequestMessages.Malformed], errors);
    }

    [FunctionalFact]
    public async Task GivenAUserToken_WhenListingTheAuditLogWithAMalformedQuery_ThenForbiddenNotBadRequest()
    {
        // Given — role checks come before binding is judged
        Authorize(TestTokens.ForRole((int)Roles.User));

        // When
        var (status, _) = await SendAsync(HttpMethod.Get, "/api/audit-log?succeeded=maybe");

        // Then
        Assert.Equal(HttpStatusCode.Forbidden, status);
    }

    [FunctionalFact]
    public async Task GivenAMalformedRequest_WhenRefused_ThenTheEnvelopeIsAFailedDataOutputWithATimestamp()
    {
        // Given
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        using var response = await Gateway.Client.GetAsync("/api/audit-log?succeeded=maybe");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        // Then
        var root = document.RootElement;
        Assert.False(root.GetProperty("success").GetBoolean());
        Assert.True(root.TryGetProperty("timestamp", out _) || root.TryGetProperty("Timestamp", out _));
    }
}
