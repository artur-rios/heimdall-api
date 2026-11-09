using System.Net;
using ArturRios.Heimdall.WebApi.Metrics;
using ArturRios.Heimdall.WebApi.Security;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.WebApi.Extensions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace ArturRios.Heimdall.WebApi.Tests;

// Who the API believes its caller is. The rate limiter partitions on the answer and every request is
// logged with it, so a caller who could choose it could dodge the one and forge the other.
//
// The hosts below cannot be the real Startup: the in-memory test server has no remote address, so
// they carry the real EdgePipeline, the second forwarded-headers pass of the standard pipeline, and
// one step in front that stamps the address a real Kestrel connection would have carried — Traefik's,
// or a stranger's.
public class TrustedProxyTests
{
    private const string Traefik = "172.18.0.2";
    private const string Caller = "203.0.113.7";
    private const string OtherVisitor = "203.0.113.8";
    private const string Forged = "198.51.100.1";

    // Inside one of the ranges Cloudflare publishes: the connection Traefik saw behind the proxy.
    private const string CloudflareEdge = "172.70.1.1";

    [UnitTheory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void GivenNothingConfigured_WhenParsing_ThenNoProxyIsTrusted(string? value)
    {
        var options = TrustedProxyOptions.Parse(value);

        Assert.False(options.Configured);
        Assert.Empty(options.InvalidEntries);
    }

    [UnitFact]
    public void GivenAddressesAndNetworks_WhenParsing_ThenEachLandsInItsOwnList()
    {
        var options = TrustedProxyOptions.Parse(" 172.18.0.2 , 10.0.0.0/8,::1, fd00::/8 ");

        Assert.True(options.Configured);
        Assert.Equal([IPAddress.Parse("172.18.0.2"), IPAddress.IPv6Loopback], options.Proxies);
        Assert.Equal(
            [System.Net.IPNetwork.Parse("10.0.0.0/8"), System.Net.IPNetwork.Parse("fd00::/8")],
            options.Networks);
        Assert.Empty(options.InvalidEntries);
    }

    [UnitFact]
    public void GivenUnusableEntries_WhenParsing_ThenTheyAreReportedAndTheRestKept()
    {
        var options = TrustedProxyOptions.Parse("traefik, 172.18.0.2, 10.0.0.0/99");

        Assert.Equal([IPAddress.Parse("172.18.0.2")], options.Proxies);
        Assert.Equal(["traefik", "10.0.0.0/99"], options.InvalidEntries);
    }

    [UnitFact]
    public async Task GivenTheTrustedProxy_WhenItForwardsACaller_ThenTheCallerIsTheClient()
    {
        await using var app = await StartAsync(Traefik, remoteAddress: Traefik);

        var client = await GetClientAsync(app, forwardedFor: Caller);

        Assert.Equal(Caller, client);
    }

    [UnitFact]
    public async Task GivenATrustedNetwork_WhenAProxyInItForwardsACaller_ThenTheCallerIsTheClient()
    {
        await using var app = await StartAsync("172.18.0.0/16", remoteAddress: Traefik);

        var client = await GetClientAsync(app, forwardedFor: Caller);

        Assert.Equal(Caller, client);
    }

    [UnitFact]
    public async Task GivenAnUntrustedConnection_WhenItSendsForwardedFor_ThenTheHeaderIsIgnored()
    {
        // A caller reaching the API directly cannot rename itself.
        await using var app = await StartAsync(Traefik, remoteAddress: Caller);

        var client = await GetClientAsync(app, forwardedFor: "198.51.100.1");

        Assert.Equal(Caller, client);
    }

    [UnitFact]
    public async Task GivenTheTrustedProxy_WhenTheCallerForgedAnEarlierHop_ThenOnlyTheHopTraefikSawIsUsed()
    {
        // Traefik appends the address it saw to whatever the caller sent. Only that last entry is
        // vouched for; the forged one ahead of it must not become the client.
        await using var app = await StartAsync(Traefik, remoteAddress: Traefik);

        var client = await GetClientAsync(app, forwardedFor: $"198.51.100.1, {Caller}");

        Assert.Equal(Caller, client);
    }

    [UnitFact]
    public async Task GivenNothingConfigured_WhenAProxyForwardsACaller_ThenTheProxyIsTheClient()
    {
        await using var app = await StartAsync(null, remoteAddress: Traefik);

        var client = await GetClientAsync(app, forwardedFor: Caller);

        Assert.Equal(Traefik, client);
    }

    [UnitTheory]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("false", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("yes", false)]
    public void GivenTheCloudflareSetting_WhenParsing_ThenOnlyTrueTurnsItOn(string? value, bool expected)
    {
        var options = TrustedProxyOptions.Parse(Traefik, value);

        Assert.Equal(expected, options.BehindCloudflare);
    }

    [UnitFact]
    public async Task GivenCloudflareInFront_WhenTraefikForwardsAVisitor_ThenTheVisitorIsTheClient()
    {
        // Behind Cloudflare, Traefik sends on what Cloudflare reported with the connection's address
        // after it. That last address is an edge, shared by unrelated visitors.
        await using var app = await StartBehindCloudflareAsync(remoteAddress: Traefik);

        var client = await GetClientAsync(app, forwardedFor: $"{Caller}, {CloudflareEdge}");

        Assert.Equal(Caller, client);
    }

    [UnitFact]
    public async Task GivenCloudflareInFront_WhenTheVisitorForgedAnEntry_ThenTheForgedEntryIsNotTheClient()
    {
        // Cloudflare adds the visitor after whatever the visitor sent, so a forged entry is always
        // further left than the one read.
        await using var app = await StartBehindCloudflareAsync(remoteAddress: Traefik);

        var client = await GetClientAsync(app, forwardedFor: $"{Forged}, {Caller}, {CloudflareEdge}");

        Assert.Equal(Caller, client);
    }

    [UnitFact]
    public async Task GivenCloudflareInFront_WhenTheVisitorIsInsideATrustedNetwork_ThenTheForgedEntryIsStillNotTheClient()
    {
        // The middleware runs twice, and a client address inside a trusted network would make the
        // second pass read one entry further left, which is the forged one.
        const string insideTrustedNetwork = "172.18.0.9";
        await using var app = await StartBehindCloudflareAsync(remoteAddress: Traefik);

        var client = await GetClientAsync(app, forwardedFor: $"{Forged}, {insideTrustedNetwork}, {CloudflareEdge}");

        Assert.Equal(insideTrustedNetwork, client);
    }

    [UnitFact]
    public async Task GivenCloudflareInFront_WhenTwoVisitorsShareAnEdge_ThenEachIsCountedApart()
    {
        await using var app = await StartBehindCloudflareAsync(remoteAddress: Traefik);

        var first = await GetAsync(app, "/partition", $"{Caller}, {CloudflareEdge}");
        var second = await GetAsync(app, "/partition", $"{OtherVisitor}, {CloudflareEdge}");

        Assert.Equal(PartitionOf(Caller), first);
        Assert.Equal(PartitionOf(OtherVisitor), second);
    }

    [UnitFact]
    public async Task GivenCloudflareInFront_WhenAVisitorForgesEntries_ThenTheirPartitionDoesNotMove()
    {
        await using var app = await StartBehindCloudflareAsync(remoteAddress: Traefik);

        var partition = await GetAsync(app, "/partition", $"{Forged}, {Caller}, {CloudflareEdge}");

        Assert.Equal(PartitionOf(Caller), partition);
    }

    [UnitFact]
    public async Task GivenTheCloudflareSetting_WhenAClientReachesTraefikDirectly_ThenTheClientIsCounted()
    {
        // From an address outside Cloudflare's ranges Traefik discards what the client sent and
        // writes the connection's address alone: one entry, and it is the client's.
        await using var app = await StartBehindCloudflareAsync(remoteAddress: Traefik);

        var client = await GetClientAsync(app, forwardedFor: Caller);
        var partition = await GetAsync(app, "/partition", Caller);

        Assert.Equal(Caller, client);
        Assert.Equal(PartitionOf(Caller), partition);
    }

    [UnitFact]
    public async Task GivenTheCloudflareSetting_WhenAnUntrustedConnectionSendsEntries_ThenTheyAreIgnored()
    {
        await using var app = await StartBehindCloudflareAsync(remoteAddress: Caller);

        var client = await GetClientAsync(app, forwardedFor: $"{Forged}, {OtherVisitor}, {CloudflareEdge}");

        Assert.Equal(Caller, client);
    }

    [UnitFact]
    public async Task GivenCloudflareNotDeclared_WhenTraefikForwardsTwoEntries_ThenTheLastIsStillTheClient()
    {
        // The setting is opt-in: a proxy that appends to what its client sent stays safe without it.
        await using var app = await StartAsync(Traefik, remoteAddress: Traefik);

        var client = await GetClientAsync(app, forwardedFor: $"{Forged}, {CloudflareEdge}");

        Assert.Equal(CloudflareEdge, client);
    }

    [UnitFact]
    public async Task GivenCloudflareDeclaredButNoProxyTrusted_WhenEntriesArrive_ThenTheConnectionIsTheClient()
    {
        await using var app = await StartAsync(null, remoteAddress: Traefik, cloudflareProxied: "true");

        var client = await GetClientAsync(app, forwardedFor: $"{Caller}, {CloudflareEdge}");

        Assert.Equal(Traefik, client);
    }

    [UnitFact]
    public async Task GivenTheTrustedProxy_WhenItForwardsTheScheme_ThenTheRequestIsHttps()
    {
        await using var app = await StartAsync(Traefik, remoteAddress: Traefik);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/scheme");
        request.Headers.Add("X-Forwarded-Proto", "https");

        var response = await app.GetTestClient().SendAsync(request);

        Assert.Equal("https", await response.Content.ReadAsStringAsync());
    }

    private static string PartitionOf(string address) =>
        RateLimitingConfiguration.RateLimitPartitionKey(IPAddress.Parse(address));

    private static Task<string> GetClientAsync(WebApplication app, string forwardedFor) =>
        GetAsync(app, "/client", forwardedFor);

    private static async Task<string> GetAsync(WebApplication app, string path, string forwardedFor)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("X-Forwarded-For", forwardedFor);

        var response = await app.GetTestClient().SendAsync(request);

        return await response.Content.ReadAsStringAsync();
    }

    private static Task<WebApplication> StartBehindCloudflareAsync(string remoteAddress) =>
        StartAsync("172.18.0.0/16", remoteAddress, cloudflareProxied: "true");

    // The real EdgePipeline and, in the application's own pipeline, the same middleware again, as the
    // standard pipeline runs it. Answers with what the request log and the rate limiter use.
    private static async Task<WebApplication> StartAsync(
        string? trustedProxies, string remoteAddress, string? cloudflareProxied = null)
    {
        var options = TrustedProxyOptions.Parse(trustedProxies, cloudflareProxied);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();

        if (options.Configured)
        {
            builder.Services.Configure<ForwardedHeadersOptions>(options.Apply);
        }

        builder.Services.AddSingleton<IStartupFilter>(new ConnectionStamp(remoteAddress));
        builder.Services.AddSingleton<IStartupFilter>(
            new EdgePipeline(MetricsOptions.Disabled, SensitiveDiagnostics.None, options));

        var app = builder.Build();
        var callers = TrustedCallerOptions.Parse(null);

        app.UseForwardedHeaders();
        app.MapGet("/client", (HttpContext context) => context.GetClientIpAddress());
        app.MapGet("/partition", (HttpContext context) => AuthRateLimitPartition.KeyFor(context, callers));
        app.MapGet("/scheme", (HttpContext context) => context.Request.Scheme);

        await app.StartAsync();

        return app;
    }

    // Registered first, so it wraps EdgePipeline: the address a real connection would carry.
    private sealed class ConnectionStamp(string remoteAddress) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextStep) =>
            {
                context.Connection.RemoteIpAddress = IPAddress.Parse(remoteAddress);

                return nextStep(context);
            });

            next(app);
        };
    }
}
