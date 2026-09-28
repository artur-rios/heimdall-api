using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;

namespace ArturRios.Heimdall.WebApi.Metrics;

/// <summary>
///     Registers the OpenTelemetry metrics pipeline and mounts its Prometheus scrape endpoint. Both
///     halves live here rather than inline in <c>Startup</c> so the tests can build a host with
///     exactly this wiring and prove which requests reach the exporter — <c>Startup</c> itself
///     cannot be exercised on the metrics port, because the in-memory test server reports a local
///     port of <c>0</c> for every request.
/// </summary>
public static class MetricsConfiguration
{
    /// <summary>The <c>service.name</c> every series carries, so dashboards can tell this API apart.</summary>
    public const string ServiceName = "heimdall-api";

    /// <summary>
    ///     Meters that ship inside the runtime and the frameworks the API already uses, subscribed to by
    ///     name. None of them costs a package: each is emitted whether or not anything listens, and
    ///     naming it here is what makes the exporter collect it.
    /// </summary>
    /// <remarks>
    ///     <list type="bullet">
    ///         <item>
    ///             <c>System.Runtime</c> — GC, heap, thread pool, exceptions, CPU. Built into .NET 9
    ///             and later, which is what replaces the OpenTelemetry.Instrumentation.Runtime package.
    ///         </item>
    ///         <item>
    ///             <c>Microsoft.AspNetCore.Server.Kestrel</c> — connections, queued requests, TLS
    ///             handshakes. <c>AddAspNetCoreInstrumentation</c> subscribes to it as well; it is named
    ///             again so removing that call would not quietly take the connection metrics with it.
    ///         </item>
    ///         <item>
    ///             <c>Microsoft.EntityFrameworkCore</c> — active contexts, queries, SaveChanges and
    ///             optimistic-concurrency failures (EF Core 9 and later).
    ///         </item>
    ///         <item>
    ///             <c>Npgsql</c> — connection pool usage, command duration and failures: the first
    ///             place a saturated or unreachable database shows.
    ///         </item>
    ///     </list>
    /// </remarks>
    private static readonly string[] BuiltInMeters =
    [
        "System.Runtime",
        "Microsoft.AspNetCore.Server.Kestrel",
        "Microsoft.EntityFrameworkCore",
        "Npgsql"
    ];

    /// <summary>
    ///     Registers the metrics pipeline, or nothing at all when <paramref name="options" /> has
    ///     metrics switched off — no meter listener, no exporter, no collection cost.
    /// </summary>
    public static IServiceCollection AddHeimdallMetrics(this IServiceCollection services, MetricsOptions options)
    {
        if (!options.Enabled)
        {
            return services;
        }

        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(ServiceName))
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddMeter(BuiltInMeters)
                .AddPrometheusExporter());

        return services;
    }

    /// <summary>
    ///     Mounts the scrape endpoint behind <see cref="MetricsOptions.IsScrapeRequest" />. It must be
    ///     called before anything else in the pipeline: the endpoint is a terminal branch, so a
    ///     request it takes never reaches CORS, rate limiting, authentication, authorization or MVC —
    ///     and, not being a controller action, it never appears in the OpenAPI document either.
    /// </summary>
    public static IApplicationBuilder UseHeimdallMetrics(this IApplicationBuilder app, MetricsOptions options)
    {
        if (!options.Enabled)
        {
            return app;
        }

        return app.UseOpenTelemetryPrometheusScrapingEndpoint(options.IsScrapeRequest);
    }
}
