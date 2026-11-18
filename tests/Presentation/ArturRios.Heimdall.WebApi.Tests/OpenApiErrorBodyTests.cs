using System.Net;
using System.Text;
using System.Text.Json;
using ArturRios.Configuration.Enums;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.WebApi.Tests.Support;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Functional;

namespace ArturRios.Heimdall.WebApi.Tests;

/// <summary>
///     Real error responses against the published document: each body's members are properties of
///     the schema the document gives that status, and its content type is one the document lists.
/// </summary>
/// <remarks>
///     A generated client reads an error body with the documented schema, and every schema in the
///     document has <c>additionalProperties: false</c>, so a member the schema does not name fails
///     the parse. One source per case: the authentication middleware's 401, the role filter's 403,
///     a handler's 404 through its status map, and MalformedRequestFilter's 400.
/// </remarks>
[Collection(nameof(FunctionalCollection))]
public class OpenApiErrorBodyTests(PostgresFixture db) : WebApiTest<Program>(EnvironmentType.Local)
{
    private const string ApplicationRoute = "/api/scopes/{scopeId}/applications/{id}";

    private static string Application(Guid scopeId, Guid id) => $"/api/scopes/{scopeId}/applications/{id}";

    [FunctionalFact]
    public async Task GivenNoToken_WhenAnAuthenticatedOperationIsCalled_ThenThe401IsTheDocumentedBody()
    {
        // Given no bearer token

        // When
        using var response = await Gateway.Client.GetAsync(Application(Guid.NewGuid(), Guid.NewGuid()));

        // Then
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await AssertDocumentedAsync(response, ApplicationRoute, "get", "401");
    }

    [FunctionalFact]
    public async Task GivenAUser_WhenAnAdministratorsOperationIsCalled_ThenThe403IsTheDocumentedBody()
    {
        // Given a caller holding the User role, which the role filter refuses
        var caller = await Seeds.PersonAsync(db, Roles.User);
        Authorize(TestTokens.For(caller.PublicId, (int)Roles.User));

        // When
        using var response = await Gateway.Client.GetAsync(Application(Guid.NewGuid(), Guid.NewGuid()));

        // Then
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await AssertDocumentedAsync(response, ApplicationRoute, "get", "403");
    }

    [FunctionalFact]
    public async Task GivenAnUnknownApplication_WhenItIsRead_ThenThe404IsTheDocumentedBody()
    {
        // Given an application id nobody holds
        var scope = await Seeds.ScopeAsync(db);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        using var response = await Gateway.Client.GetAsync(Application(scope.PublicId, Guid.NewGuid()));

        // Then
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertDocumentedAsync(response, ApplicationRoute, "get", "404");
    }

    [FunctionalFact]
    public async Task GivenAMalformedBody_WhenAScopeIsCreated_ThenThe400IsTheDocumentedBody()
    {
        // Given a body that is not JSON
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));
        using var body = new StringContent("{ not json", Encoding.UTF8, "application/json");

        // When
        using var response = await Gateway.Client.PostAsync("/api/scopes", body);

        // Then
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertDocumentedAsync(response, "/api/scopes", "post", "400");
    }

    [FunctionalFact]
    public async Task GivenABodyNoFormatterReads_WhenAScopeIsCreated_ThenThe415HasNoBodyAsDocumented()
    {
        // Given a body sent as text/plain, which no input formatter reads
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));
        using var body = new StringContent("{}", Encoding.UTF8, "text/plain");

        // When
        using var response = await Gateway.Client.PostAsync("/api/scopes", body);

        // Then: MVC's UnsupportedContentTypeFilter, ahead of MalformedRequestFilter, with no body
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(
            Environment.GetEnvironmentVariable("HEIMDALL_OPENAPI_DOCUMENT")
            ?? Path.Combine(Repository.Root, "docs", "openapi", "heimdall.json")));
        var documented = document.RootElement.GetProperty("paths").GetProperty("/api/scopes").GetProperty("post")
            .GetProperty("responses").GetProperty("415");

        Assert.False(documented.TryGetProperty("content", out _));
    }

    private static async Task AssertDocumentedAsync(HttpResponseMessage response, string route, string method, string status)
    {
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(
            Environment.GetEnvironmentVariable("HEIMDALL_OPENAPI_DOCUMENT")
            ?? Path.Combine(Repository.Root, "docs", "openapi", "heimdall.json")));
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var content = document.RootElement.GetProperty("paths").GetProperty(route).GetProperty(method)
            .GetProperty("responses").GetProperty(status).GetProperty("content");
        var mediaTypes = content.EnumerateObject().Select(media => media.Name).ToList();
        var schemaName = content.EnumerateObject().First().Value.GetProperty("schema").GetProperty("$ref").GetString()!
            .Split('/')[^1];
        var properties = document.RootElement.GetProperty("components").GetProperty("schemas").GetProperty(schemaName)
            .GetProperty("properties").EnumerateObject().Select(property => property.Name).ToHashSet();

        Assert.Contains(response.Content.Headers.ContentType?.MediaType, mediaTypes);
        Assert.All(body.RootElement.EnumerateObject(), member => Assert.Contains(member.Name, properties));
        Assert.Contains("errors", body.RootElement.EnumerateObject().Select(member => member.Name));
    }
}
