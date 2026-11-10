namespace ArturRios.Heimdall.Query.HealthChecks;

/// <summary>
///     Canonical health status strings used by the internal health report (UC-30, FR-HC-05). Kept as
///     literals — not an enum — so they serialize as <c>"Healthy"</c> / <c>"Unhealthy"</c> without
///     depending on global JSON enum-converter configuration.
/// </summary>
public static class HealthStatuses
{
    public const string Healthy = "Healthy";
    public const string Degraded = "Degraded";
    public const string Unhealthy = "Unhealthy";

    /// <summary>The API was given no settings for this service, so there was nothing to check.</summary>
    public const string NotConfigured = "NotConfigured";
}
