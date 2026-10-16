using System.Net;
using System.Text.Json;
using ArturRios.Heimdall.WebApi.Security;
using ArturRios.Heimdall.WebApi.Tests.Support;
using ArturRios.Util.Test.Attributes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Matching;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace ArturRios.Heimdall.WebApi.Tests;

// Functional tests for the developer exception page under NFR-28's one decision. In Development the
// framework adds the page by itself, inside the startup filters and ahead of the standard pipeline,
// so these boot the real host and fail a request where only that page can catch it: in routing,
// ahead of the library's ExceptionMiddleware. On a public host without the override the answer is
// the API's own 500 envelope; with the override, or on localhost, it is the page.
[Collection(nameof(FunctionalCollection))]
public class DeveloperExceptionPageTests
{
    private const string FaultHeader = "X-Heimdall-Test-Fault";
    private const string FaultMessage = "Injected routing fault";
    private const string InternalServerError = "Internal server error, please try again later";
    private const string PublicHost = "heimdall-api-dev.example.com";

    private static readonly string[] ReadVariables =
    [
        "ASPNETCORE_ENVIRONMENT",
        PublicHostSettings.PublicHostVariable,
        SensitiveDiagnosticsGuard.AllowVariable
    ];

    /// <summary>
    ///     Fails routing for any request carrying <see cref="FaultHeader" />. A matcher policy runs
    ///     inside EndpointRoutingMiddleware, which the host places between the framework's developer
    ///     exception page and the standard pipeline.
    /// </summary>
    private sealed class FaultingMatcherPolicy : MatcherPolicy, IEndpointSelectorPolicy
    {
        public override int Order => 0;

        public bool AppliesToEndpoints(IReadOnlyList<Endpoint> endpoints) => true;

        public Task ApplyAsync(HttpContext httpContext, CandidateSet candidates) =>
            httpContext.Request.Headers.ContainsKey(FaultHeader)
                ? throw new InvalidOperationException(FaultMessage)
                : Task.CompletedTask;
    }

    /// <summary>
    ///     Starts a Development host with <paramref name="publicHost" /> and <paramref name="allow" />
    ///     set while it starts, which is when Startup reads them, and sends one failing request.
    /// </summary>
    private static async Task<(HttpStatusCode Status, string? MediaType, string Body)> FailARequestAsync(
        string? publicHost, string? allow)
    {
        var saved = ReadVariables.ToDictionary(name => name, Environment.GetEnvironmentVariable);

        try
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "development");
            Environment.SetEnvironmentVariable(PublicHostSettings.PublicHostVariable, publicHost);
            Environment.SetEnvironmentVariable(SensitiveDiagnosticsGuard.AllowVariable, allow);

            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder
                .UseEnvironment("Development")
                .ConfigureTestServices(services => services.AddSingleton<MatcherPolicy, FaultingMatcherPolicy>()));

            using var client = factory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/healthcheck");
            request.Headers.Add(FaultHeader, "1");
            request.Headers.Add("Accept", "text/html");

            using var response = await client.SendAsync(request);

            return (response.StatusCode, response.Content.Headers.ContentType?.MediaType,
                await response.Content.ReadAsStringAsync());
        }
        finally
        {
            foreach (var (name, value) in saved)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }
    }

    [FunctionalFact]
    public async Task GivenDevelopmentOnAPublicHostWithoutTheOverride_WhenARequestFails_ThenTheApisOwnErrorIsServed()
    {
        // Given / When
        var (status, mediaType, body) = await FailARequestAsync(PublicHost, null);

        // Then — the envelope ExceptionMiddleware writes, with nothing of the exception in it
        Assert.Equal(HttpStatusCode.InternalServerError, status);
        Assert.Equal("application/json", mediaType);
        Assert.DoesNotContain(FaultMessage, body);
        Assert.DoesNotContain(nameof(InvalidOperationException), body);

        using var document = JsonDocument.Parse(body);
        var errors = document.RootElement.GetProperty("errors").EnumerateArray().Select(error => error.GetString());
        Assert.Equal([InternalServerError], errors);
    }

    [FunctionalFact]
    public async Task GivenDevelopmentOnAPublicHostWithTheOverride_WhenARequestFails_ThenTheDeveloperPageIsServed()
    {
        // Given / When
        var (status, mediaType, body) = await FailARequestAsync(PublicHost, "true");

        // Then
        Assert.Equal(HttpStatusCode.InternalServerError, status);
        Assert.Equal("text/html", mediaType);
        Assert.Contains(FaultMessage, body);
    }

    [FunctionalFact]
    public async Task GivenDevelopmentOnLocalhost_WhenARequestFails_ThenTheDeveloperPageIsServed()
    {
        // Given / When
        var (status, mediaType, body) = await FailARequestAsync("localhost", null);

        // Then
        Assert.Equal(HttpStatusCode.InternalServerError, status);
        Assert.Equal("text/html", mediaType);
        Assert.Contains(FaultMessage, body);
    }
}
