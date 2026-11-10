using System.Net;
using System.Reflection;
using ArturRios.Configuration.Enums;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.WebApi.Controllers;
using ArturRios.Heimdall.WebApi.Metrics;
using ArturRios.Heimdall.WebApi.Security;
using ArturRios.Heimdall.WebApi.Tests.Support;
using ArturRios.Output;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Functional;
using ArturRios.Util.WebApi.Configuration;
using ArturRios.Util.WebApi.Security.Extensions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArturRios.Heimdall.WebApi.Tests;

// Joining the collection is what matters here: xUnit initializes the collection fixture before
// constructing any test class in it, so the container is running and migrated by the time the base
// constructor boots the API and reads the connection details the fixture publishes.
[Collection(nameof(FunctionalCollection))]
public class HealthCheckTests() : WebApiTest<Program>(EnvironmentType.Local)
{
    private const string HealthCheckRoute = "/api/healthcheck";

    // The root addresses the container probe and yggdrasil's status catalog call. They stay outside
    // the published contract, so only these tests hold them in place.
    private const string RootHealthCheckRoute = "/healthcheck";

    // UC-30 liveness main flow (FR-HC-01) — public, no authentication.
    [FunctionalFact]
    public async Task GivenApiWorking_WhenHealthCheckEndpointCalled_ThenEndpointReturnsOk()
    {
        var output = await Gateway.GetAsync<DataOutput<string>>(HealthCheckRoute);

        Assert.Equal(HttpStatusCode.OK, output.StatusCode);
        Assert.NotNull(output.Body?.Data);
        Assert.Equal("Hello world!", output.Body.Data);
        Assert.Equal("Heimdall API is working.", output.Body.Messages.First());
    }

    // The probes reach the API on its own port, not through Traefik, so they keep the root address.
    [FunctionalFact]
    public async Task GivenApiWorking_WhenRootHealthCheckCalled_ThenEndpointReturnsOk()
    {
        var output = await Gateway.GetAsync<DataOutput<string>>(RootHealthCheckRoute);

        Assert.Equal(HttpStatusCode.OK, output.StatusCode);
        Assert.Equal("Hello world!", output.Body?.Data);
    }

    // Case-insensitive, as routing is: the address has been documented both as /HealthCheck and as
    // /healthcheck.
    [FunctionalFact]
    public async Task GivenApiWorking_WhenRootHealthCheckCalledInPascalCase_ThenEndpointReturnsOk()
    {
        var output = await Gateway.GetAsync<DataOutput<string>>("/HealthCheck");

        Assert.Equal(HttpStatusCode.OK, output.StatusCode);
        Assert.Equal("Hello world!", output.Body?.Data);
    }

    [FunctionalTheory]
    [InlineData("/healthcheck")]
    [InlineData("/healthcheck/ready")]
    public async Task GivenRootHealthcheckAndReady_WhenRequested_ThenStillServed(string route)
    {
        var response = await Gateway.Client.GetAsync(route);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // Authenticated callers are the ones who can see the route is gone: see the test below for the rest.
    [FunctionalTheory]
    [InlineData("/api/healthcheck/detailed", (int)Roles.SystemAdmin)]
    [InlineData("/api/healthcheck/detailed", (int)Roles.User)]
    [InlineData("/healthcheck/detailed", (int)Roles.SystemAdmin)]
    [InlineData("/healthcheck/detailed", (int)Roles.User)]
    public async Task GivenAdminRoute_WhenRequestedAnyAuth_Then404(string route, int role)
    {
        // Given
        Authorize(TestTokens.ForRole(role));

        // When
        var response = await Gateway.Client.GetAsync(route);

        // Then
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // AuthenticationMiddleware refuses an anonymous request for any route not declared anonymous, and
    // a route that does not exist is not declared anonymous, so it answers 401 before routing can say
    // 404: the same answer as for every other address the API does not have (MetricsEndpointTests).
    [FunctionalTheory]
    [InlineData("/api/healthcheck/detailed")]
    [InlineData("/healthcheck/detailed")]
    public async Task GivenAdminRouteAndNoToken_WhenRequested_ThenUnauthorizedLikeAnyUnknownRoute(string route)
    {
        var response = await Gateway.Client.GetAsync(route);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}

// The same routes without a database: the real EdgePipeline, with a terminal step that answers with the
// path it was handed, so a test sees exactly what the rewrite did and whether anything got that far.
public class HealthCheckRoutingTests
{
    private const int MetricsPort = MetricsOptions.DefaultPort;
    private const int ApplicationPort = 8080;
    private const string ReachedTheApi = "X-Reached-The-Api";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [UnitTheory]
    [InlineData("/healthcheck", "/api/healthcheck")]
    [InlineData("/healthcheck/", "/api/healthcheck")]
    [InlineData("/HealthCheck", "/api/healthcheck")]
    [InlineData("/healthcheck/ready", "/api/healthcheck/ready")]
    [InlineData("/healthcheck/ready/", "/api/healthcheck/ready")]
    [InlineData("/HealthCheck/Ready", "/api/healthcheck/Ready")]
    public async Task GivenRootHealthcheckAndReady_WhenRequestedOnTheApplicationPort_ThenRewrittenToTheApi(
        string path, string rewritten)
    {
        // Given
        await using var app = await StartAsync(ApplicationPort);

        // When
        var response = await app.GetTestClient().GetAsync(path, Token);

        // Then
        Assert.Equal(rewritten, await response.Content.ReadAsStringAsync(Token));
    }

    [UnitTheory]
    [InlineData("/healthcheck/detailed")]
    [InlineData("/healthcheck/detailed/")]
    [InlineData("/HealthCheck/Detailed")]
    public async Task GivenRootAdminRoute_WhenRequestedOnTheApplicationPort_ThenNotRewritten(string path)
    {
        // Given
        await using var app = await StartAsync(ApplicationPort);

        // When
        var response = await app.GetTestClient().GetAsync(path, Token);

        // Then the path is left as it came, so it can reach no controller
        Assert.Equal(path, await response.Content.ReadAsStringAsync(Token));
    }

    [UnitTheory]
    [InlineData("/healthcheck/detailed")]
    [InlineData("/api/healthcheck/detailed")]
    [InlineData("/healthcheck")]
    [InlineData("/healthcheck/ready")]
    public async Task GivenAnyHealthRoute_WhenRequestedOnTheMetricsPort_ThenClosedAheadOfTheRewrite(string path)
    {
        // Given
        await using var app = await StartAsync(MetricsPort);

        // When
        var response = await app.GetTestClient().GetAsync(path, Token);

        // Then
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.False(response.Headers.Contains(ReachedTheApi));
    }

    [UnitFact]
    public void GivenTheController_WhenItsRoutesAreRead_ThenOnlyLivenessAndReadinessRemain()
    {
        var routes = typeof(HealthCheckController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .SelectMany(method => method.GetCustomAttributes<RouteAttribute>())
            .Select(route => route.Template)
            .Order();

        Assert.Equal(["", "ready"], routes);
    }

    private static async Task<WebApplication> StartAsync(int localPort)
    {
        var metrics = MetricsOptions.Parse(null);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddHeimdallMetrics(metrics);
        builder.Services.AddSingleton<IStartupFilter>(new ConnectionStamp(localPort));
        builder.Services.AddSingleton<IStartupFilter>(
            new EdgePipeline(metrics, SensitiveDiagnostics.None, TrustedProxyOptions.Parse(null)));

        var app = builder.Build();

        app.Run(context =>
        {
            context.Response.Headers[ReachedTheApi] = "true";

            return context.Response.WriteAsync(context.Request.Path.Value!);
        });

        await app.StartAsync();

        return app;
    }

    // Registered first, so it wraps EdgePipeline: the port a real connection would carry.
    private sealed class ConnectionStamp(int localPort) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextStep) =>
            {
                context.Connection.LocalPort = localPort;

                return nextStep(context);
            });

            next(app);
        };
    }
}
