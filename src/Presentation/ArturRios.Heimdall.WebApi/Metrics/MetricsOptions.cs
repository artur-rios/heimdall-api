namespace ArturRios.Heimdall.WebApi.Metrics;

/// <summary>
///     Where the Prometheus scrape endpoint is served, read once at start-up from
///     <c>HEIMDALL_METRICS_PORT</c>. Decides both whether the exporter is registered at all and which
///     requests it answers — see <see cref="IsScrapeRequest" />.
/// </summary>
/// <remarks>
///     <para>
///         The endpoint is told apart from the public API by the <em>port the connection arrived
///         on</em>, and by nothing a caller can influence. The API runs behind Traefik, which
///         forwards the client's own <c>Host</c> header, so a check on the host name would be a
///         check on a value the attacker chooses. The local port is decided by the socket instead:
///         Traefik reaches the container on the public port, Prometheus reaches it directly on this
///         one over a private Docker network, and no request can arrive on one while claiming to
///         be on the other.
///     </para>
///     <para>
///         That only holds while this port is never published. Kestrel listens on it because the
///         image's <c>ASPNETCORE_HTTP_PORTS</c> says so, and Compose deliberately maps only the
///         public port to the host — see the Dockerfile and docker-compose.yml.
///     </para>
/// </remarks>
public sealed class MetricsOptions
{
    /// <summary>The port Prometheus scrapes on. <c>0</c> switches the exporter off.</summary>
    public const string PortVariable = "HEIMDALL_METRICS_PORT";

    /// <summary>
    ///     The exporter's conventional port, the one Prometheus's own default-port allocations list for
    ///     the OpenTelemetry Prometheus exporter — so a scrape configuration written from the
    ///     convention needs no change.
    /// </summary>
    public const int DefaultPort = 9464;

    /// <summary>The scrape path. Fixed: Prometheus assumes it, so making it configurable buys nothing.</summary>
    public const string ScrapePath = "/metrics";

    /// <summary>The detailed health report's path, on this port and no other.</summary>
    public const string InternalHealthPath = "/internal/health";

    /// <summary>Metrics switched off: no exporter registered, no request ever treated as a scrape.</summary>
    public static MetricsOptions Disabled { get; } = new() { Port = 0 };

    /// <summary>The port the scrape endpoint answers on, or <c>0</c> when metrics are switched off.</summary>
    public int Port { get; private init; }

    /// <summary>Whether the exporter is registered and the scrape endpoint served.</summary>
    public bool Enabled => Port > 0;

    /// <summary>
    ///     The configured value when it was unusable and <see cref="DefaultPort" /> was applied instead;
    ///     <c>null</c> otherwise. Kept so start-up can say so, instead of silently scraping somewhere
    ///     the operator did not ask for.
    /// </summary>
    public string? InvalidValue { get; private init; }

    public static MetricsOptions FromEnvironment() => Parse(Environment.GetEnvironmentVariable(PortVariable));

    /// <summary>
    ///     Interprets a <see cref="PortVariable" /> value. Split from <see cref="FromEnvironment" /> so
    ///     it can be tested without mutating the process environment the functional suite's hosts read.
    /// </summary>
    /// <remarks>
    ///     A blank value means "the default", as it does for every other optional variable here:
    ///     Compose hands the container an empty string for anything the env file leaves unset, and
    ///     Windows cannot hold an empty variable at all, so reading blank as "off" would make the
    ///     same env file mean different things on different hosts. Switching metrics off is the
    ///     explicit <c>0</c>.
    /// </remarks>
    public static MetricsOptions Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return new MetricsOptions { Port = DefaultPort };
        }

        if (int.TryParse(value.Trim(), out var port) && port is >= 0 and <= 65535)
        {
            return new MetricsOptions { Port = port };
        }

        return new MetricsOptions { Port = DefaultPort, InvalidValue = value };
    }

    /// <summary>
    ///     Whether the connection arrived on the metrics port. Nothing but the scrape and the
    ///     internal health report is served there.
    /// </summary>
    /// <remarks>
    ///     <see cref="Enabled" /> is checked first and is not redundant. An in-memory test host
    ///     reports a local port of <c>0</c>, so without it a disabled configuration (port <c>0</c>)
    ///     would match every request there.
    /// </remarks>
    public bool IsMetricsPort(HttpContext context) => Enabled && context.Connection.LocalPort == Port;

    /// <summary>
    ///     The predicate the scrape endpoint is mounted behind: the metrics path, arriving on the
    ///     metrics port. The same path on the public port falls through to the rest of the pipeline,
    ///     which has no such route.
    /// </summary>
    public bool IsScrapeRequest(HttpContext context) =>
        IsMetricsPort(context) && context.Request.Path.Equals(ScrapePath, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    ///     The predicate the health report is mounted behind: a <c>GET</c> of its path, arriving on
    ///     the metrics port. On the public port the path is one the API does not have.
    /// </summary>
    public bool IsInternalHealthRequest(HttpContext context) =>
        IsMetricsPort(context)
        && HttpMethods.IsGet(context.Request.Method)
        && context.Request.Path.Equals(InternalHealthPath, StringComparison.OrdinalIgnoreCase);
}
