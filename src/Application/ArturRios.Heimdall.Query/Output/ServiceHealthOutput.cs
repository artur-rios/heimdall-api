namespace ArturRios.Heimdall.Query.Output;

/// <summary>
///     Status of a single verified service in the detailed health check response (UC-30, FR-HC-04).
/// </summary>
public class ServiceHealthOutput
{
    /// <summary>Name of the verified service (e.g. <c>Database</c>).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Individual status — <c>Healthy</c> or <c>Unhealthy</c>.</summary>
    public string Status { get; set; } = string.Empty;
}
