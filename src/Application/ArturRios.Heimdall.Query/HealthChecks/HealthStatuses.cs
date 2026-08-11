namespace ArturRios.Heimdall.Query.HealthChecks;

/// <summary>
///     Canonical health status strings used by the detailed health check response contract (UC-30,
///     FR-HC-05). Kept as literals — not an enum — so they serialize as <c>"Healthy"</c> /
///     <c>"Unhealthy"</c> without depending on global JSON enum-converter configuration.
/// </summary>
public static class HealthStatuses
{
    public const string Healthy = "Healthy";
    public const string Unhealthy = "Unhealthy";
}
