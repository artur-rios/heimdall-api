using System.Net;
using System.Text.Json;
using ArturRios.Configuration.Enums;
using ArturRios.Heimdall.WebApi.Tests.Support;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Functional;

namespace ArturRios.Heimdall.WebApi.Tests;

/// <summary>
///     The running API's own OpenAPI document, served by Util.WebApi's standard pipeline in Local.
/// </summary>
/// <remarks>
///     OpenApiContractTests covers the published document, which the generator tool produces without
///     running <c>Startup</c>. These cover the wiring only <c>Startup</c> has: that the document is
///     reachable without a token, and that handing <c>SwaggerConfiguration</c> to the library builds
///     a document at all — a second "Bearer" definition would throw when it is first requested.
/// </remarks>
[Collection(nameof(FunctionalCollection))]
public class SwaggerEndpointTests() : WebApiTest<Program>(EnvironmentType.Local)
{
    private const string DocumentRoute = "/swagger/v1/swagger.json";

    [FunctionalFact]
    public async Task GivenNoToken_WhenSwaggerDocumentRequested_ThenItIsServed()
    {
        var response = await Gateway.Client.GetAsync(DocumentRoute);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenSwaggerDocument_WhenRead_ThenItIsHeimdallsWithItsBearerScheme()
    {
        var response = await Gateway.Client.GetAsync(DocumentRoute);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;

        Assert.Equal("Heimdall API", root.GetProperty("info").GetProperty("title").GetString());

        var bearer = root.GetProperty("components").GetProperty("securitySchemes").GetProperty("Bearer");

        Assert.Equal("bearer", bearer.GetProperty("scheme").GetString());
    }

    // The version is the Swagger middleware's option, not the generator's, so the published document
    // being 3.1 says nothing about this one: Startup registers it separately. The envelope's `data`
    // shows the nullable-reference filter ran under it, in 3.1's form.
    [FunctionalFact]
    public async Task GivenSwaggerDocument_WhenRead_ThenItIsOpenApi31WithNullableReferences()
    {
        var response = await Gateway.Client.GetAsync(DocumentRoute);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;

        Assert.StartsWith("3.1.", root.GetProperty("openapi").GetString());

        var data = root.GetProperty("components").GetProperty("schemas")
            .GetProperty("ScopeOutputDataOutput").GetProperty("properties").GetProperty("data");
        var anyOf = data.GetProperty("anyOf");

        Assert.Equal(2, anyOf.GetArrayLength());
        Assert.Equal("#/components/schemas/ScopeOutput", anyOf[0].GetProperty("$ref").GetString());
        Assert.Equal("null", anyOf[1].GetProperty("type").GetString());
    }

    // The response filters run under Startup too: the readiness probe's 503 and the rate limiter's 429
    // are documented, and no envelope is offered as text/plain.
    [FunctionalFact]
    public async Task GivenSwaggerDocument_WhenRead_ThenItDocumentsTheProbeAndRateLimitResponses()
    {
        var response = await Gateway.Client.GetAsync(DocumentRoute);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var paths = document.RootElement.GetProperty("paths");

        var ready = paths.GetProperty("/api/healthcheck/ready").GetProperty("get").GetProperty("responses");
        var login = paths.GetProperty("/api/auth/login").GetProperty("post").GetProperty("responses");

        Assert.True(ready.GetProperty("503").GetProperty("content").TryGetProperty("application/json", out _));
        Assert.False(ready.GetProperty("200").GetProperty("content").TryGetProperty("text/plain", out _));
        Assert.True(login.GetProperty("429").GetProperty("content").TryGetProperty("application/json", out _));
    }
}
