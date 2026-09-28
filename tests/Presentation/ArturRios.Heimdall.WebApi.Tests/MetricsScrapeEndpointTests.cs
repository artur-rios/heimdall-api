using System.Net;
using ArturRios.Heimdall.WebApi.Metrics;
using ArturRios.Util.Test.Attributes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;

namespace ArturRios.Heimdall.WebApi.Tests;

// The Prometheus scrape endpoint is kept off the public port by the port the connection arrived on,
// and by nothing else: the API sits behind Traefik, which forwards the client's own Host header, so
// the one property a caller cannot choose is the socket it reached. These tests pin that rule.
//
// They cannot run against the real Startup. The in-memory test server reports a local port of 0 for
// every request, so the functional host can only ever prove the negative (MetricsEndpointTests). The
// hosts below are built from the same MetricsConfiguration calls Startup makes, with one middleware
// in front that stamps the local port a real Kestrel connection would have carried.
public class MetricsScrapeEndpointTests
{
    private const int PublicPort = 8080;

    [UnitTheory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void GivenNoPortConfigured_WhenParsing_ThenTheDefaultPortIsUsed(string? value)
    {
        // Blank is "the default", not "off": Compose passes an empty string for anything the env file
        // leaves unset, so reading blank as off would silently disable metrics in every deployment.
        var options = MetricsOptions.Parse(value);

        Assert.True(options.Enabled);
        Assert.Equal(MetricsOptions.DefaultPort, options.Port);
        Assert.Null(options.InvalidValue);
    }

    [UnitFact]
    public void GivenAPortConfigured_WhenParsing_ThenThatPortIsUsed()
    {
        var options = MetricsOptions.Parse("9100");

        Assert.True(options.Enabled);
        Assert.Equal(9100, options.Port);
        Assert.Null(options.InvalidValue);
    }

    [UnitFact]
    public void GivenZero_WhenParsing_ThenMetricsAreSwitchedOff()
    {
        var options = MetricsOptions.Parse("0");

        Assert.False(options.Enabled);
        Assert.Null(options.InvalidValue);
    }

    [UnitTheory]
    [InlineData("metrics")]
    [InlineData("-1")]
    [InlineData("65536")]
    public void GivenAnUnusableValue_WhenParsing_ThenTheDefaultIsUsedAndTheValueReported(string value)
    {
        var options = MetricsOptions.Parse(value);

        Assert.True(options.Enabled);
        Assert.Equal(MetricsOptions.DefaultPort, options.Port);
        Assert.Equal(value, options.InvalidValue);
    }

    [UnitFact]
    public void GivenMetricsPort_WhenMetricsPathRequested_ThenItIsAScrape()
    {
        var options = MetricsOptions.Parse(null);

        Assert.True(options.IsScrapeRequest(Request(MetricsOptions.DefaultPort, "/metrics")));
    }

    [UnitFact]
    public void GivenPublicPort_WhenMetricsPathRequested_ThenItIsNotAScrape()
    {
        // The case the whole design exists for: the path alone must never be enough.
        var options = MetricsOptions.Parse(null);

        Assert.False(options.IsScrapeRequest(Request(PublicPort, "/metrics")));
    }

    [UnitFact]
    public void GivenMetricsPort_WhenAnotherPathRequested_ThenItIsNotAScrape()
    {
        var options = MetricsOptions.Parse(null);

        Assert.False(options.IsScrapeRequest(Request(MetricsOptions.DefaultPort, "/healthcheck")));
    }

    [UnitFact]
    public void GivenMetricsSwitchedOff_WhenLocalPortIsZero_ThenItIsNotAScrape()
    {
        // Port 0 is "off", and 0 is also the local port an in-memory host reports. Comparing ports
        // alone would turn a disabled configuration into one that serves /metrics on every request.
        var options = MetricsOptions.Parse("0");

        Assert.False(options.IsScrapeRequest(Request(0, "/metrics")));
    }

    [UnitFact]
    public async Task GivenMetricsPort_WhenMetricsRequested_ThenPrometheusTextIsServed()
    {
        await using var app = await StartAsync(MetricsOptions.Parse(null), MetricsOptions.DefaultPort);
        using var client = app.GetTestClient();

        var response = await client.GetAsync("/metrics");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains($"service_name=\"{MetricsConfiguration.ServiceName}\"", body);
    }

    [UnitFact]
    public async Task GivenPublicPort_WhenMetricsRequested_ThenNotFound()
    {
        await using var app = await StartAsync(MetricsOptions.Parse(null), PublicPort);
        using var client = app.GetTestClient();

        var response = await client.GetAsync("/metrics");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [UnitFact]
    public async Task GivenMetricsSwitchedOff_WhenMetricsRequestedOnAnyPort_ThenNotFound()
    {
        await using var app = await StartAsync(MetricsOptions.Parse("0"), MetricsOptions.DefaultPort);
        using var client = app.GetTestClient();

        var response = await client.GetAsync("/metrics");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static DefaultHttpContext Request(int localPort, string path)
    {
        var context = new DefaultHttpContext();
        context.Connection.LocalPort = localPort;
        context.Request.Path = path;

        return context;
    }

    // A host with Startup's metrics wiring and nothing else, so whatever the scrape branch does not
    // take reaches the terminal 404 below — standing in for the rest of the API, which has no
    // /metrics route either.
    private static async Task<WebApplication> StartAsync(MetricsOptions options, int localPort)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddHeimdallMetrics(options);

        var app = builder.Build();

        app.Use((context, next) =>
        {
            context.Connection.LocalPort = localPort;

            return next(context);
        });
        app.UseHeimdallMetrics(options);
        app.Run(context =>
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;

            return Task.CompletedTask;
        });

        await app.StartAsync();

        return app;
    }
}
