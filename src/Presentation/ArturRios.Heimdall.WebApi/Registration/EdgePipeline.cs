using ArturRios.Data.Relational.Core.DependencyInjection;
using ArturRios.Heimdall.WebApi.Metrics;
using ArturRios.Heimdall.WebApi.Security;
using ArturRios.Util.WebApi.Configuration;
using ArturRios.Util.WebApi.Security.Extensions;
using Microsoft.AspNetCore.Rewrite;

namespace ArturRios.Heimdall.WebApi;

/// <summary>
///     The middlewares that must run ahead of the standard pipeline. A startup filter is how they
///     get there: ASP.NET Core wraps the application's whole pipeline in it, so what it adds runs
///     before anything <c>Build</c> adds.
/// </summary>
internal sealed class EdgePipeline(MetricsOptions metrics, SensitiveDiagnostics diagnostics) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        // First, ahead of everything else — the standard pipeline's ExceptionMiddleware and
        // AuthenticationMiddleware included. The scrape endpoint is a terminal branch taken only on
        // the metrics port, so Prometheus is never asked for a token, never rate limited, never
        // subject to CORS, and the endpoint never reaches the OpenAPI document. Every request on
        // the public port falls straight through, /metrics included.
        app.UseHeimdallMetrics(metrics);

        // The shared decision, never the environment directly: on a public host without the
        // override it is off in Development too (NFR-28). This call is what Local needs; in
        // Development the host adds its own page further in, which SuppressedDeveloperPageFilter
        // holds to the same decision.
        if (diagnostics.DeveloperExceptionPage)
        {
            app.UseDeveloperExceptionPage();
        }

        // Ahead of HTTPS redirection, which decides on the scheme: behind Traefik every request
        // arrives as http, and only X-Forwarded-Proto says the caller used https. The standard
        // pipeline runs the same middleware again first thing; by then the trusted hop has been
        // consumed and the connection's address is no longer a proxy's, so it changes nothing.
        app.UseForwardedHeaders();

        app.UseHttpsRedirection();

        // The health check moved under api/ so the web UI can reach it through its own host; the
        // container probes and yggdrasil's status catalog still call the root addresses. Rewritten
        // here, ahead of routing and authentication, so the alias runs the same endpoint with the
        // same authorization, and stays out of the OpenAPI document.
        app.UseRewriter(new RewriteOptions()
            .AddRewrite("(?i)^healthcheck(/detailed|/ready)?/?$", "api/healthcheck$1", skipRemainingRules: true));

        next(app);
    };
}
