using System.Net;
using ArturRios.Configuration.Enums;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.WebApi.Tests.Support;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Functional;

namespace ArturRios.Heimdall.WebApi.Tests;

// The whole API, as Startup builds it, with metrics at their default: enabled, on port 9464. The
// in-memory host this runs on is not port 9464 — it reports 0 for every request — so it stands where
// Traefik's traffic stands: on a port that is not the metrics one. What it proves is that /metrics is
// not served there, however it is asked for. That the endpoint does answer on its own port is shown
// by MetricsScrapeEndpointTests, over the same wiring.
[Collection(nameof(FunctionalCollection))]
public class MetricsEndpointTests() : WebApiTest<Program>(EnvironmentType.Local)
{
    [FunctionalFact]
    public async Task GivenPublicPortAndNoToken_WhenMetricsRequested_ThenUnauthorized()
    {
        // AuthenticationMiddleware refuses an anonymous request for any route not declared anonymous,
        // and a route that does not exist at all is not declared anonymous — so it answers 401 before
        // routing gets to say 404. That is what Prometheus would get if it were pointed at this port.
        var response = await Gateway.Client.GetAsync("/metrics");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenPublicPortAndSystemAdmin_WhenMetricsRequested_ThenNotFound()
    {
        // With authentication out of the way, what is left is the route itself, and there is none:
        // the most privileged caller on the public port still cannot read the metrics.
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        var response = await Gateway.Client.GetAsync("/metrics");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenPublicPort_WhenMetricsRequestedForTheMetricsHost_ThenNotFound()
    {
        // The Host header is the caller's to choose, and Traefik passes it through. Naming the
        // container's private address and port in it must change nothing.
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        using var request = new HttpRequestMessage(HttpMethod.Get, "/metrics");
        request.Headers.Host = "heimdall-api:9464";

        var response = await Gateway.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
