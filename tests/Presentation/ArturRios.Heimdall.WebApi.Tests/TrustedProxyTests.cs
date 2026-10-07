using System.Net;
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
// they carry Startup's forwarded-headers wiring with one middleware in front that stamps the address
// a real Kestrel connection would have carried — Traefik's, or a stranger's.
public class TrustedProxyTests
{
    private const string Traefik = "172.18.0.2";
    private const string Caller = "203.0.113.7";

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

    [UnitFact]
    public async Task GivenTheTrustedProxy_WhenItForwardsTheScheme_ThenTheRequestIsHttps()
    {
        await using var app = await StartAsync(Traefik, remoteAddress: Traefik);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/scheme");
        request.Headers.Add("X-Forwarded-Proto", "https");

        var response = await app.GetTestClient().SendAsync(request);

        Assert.Equal("https", await response.Content.ReadAsStringAsync());
    }

    private static async Task<string> GetClientAsync(WebApplication app, string forwardedFor)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/client");
        request.Headers.Add("X-Forwarded-For", forwardedFor);

        var response = await app.GetTestClient().SendAsync(request);

        return await response.Content.ReadAsStringAsync();
    }

    // Startup's wiring — TrustedProxyOptions applied to ForwardedHeadersOptions, UseForwardedHeaders
    // ahead of anything reading the address — answering with the client address the request log uses.
    private static async Task<WebApplication> StartAsync(string? trustedProxies, string remoteAddress)
    {
        var options = TrustedProxyOptions.Parse(trustedProxies);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();

        if (options.Configured)
        {
            builder.Services.Configure<ForwardedHeadersOptions>(options.Apply);
        }

        var app = builder.Build();

        app.Use((context, next) =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse(remoteAddress);

            return next(context);
        });
        app.UseForwardedHeaders();
        app.MapGet("/client", (HttpContext context) => context.GetClientIpAddress());
        app.MapGet("/scheme", (HttpContext context) => context.Request.Scheme);

        await app.StartAsync();

        return app;
    }
}
