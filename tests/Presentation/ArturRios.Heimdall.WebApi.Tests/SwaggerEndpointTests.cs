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
}
