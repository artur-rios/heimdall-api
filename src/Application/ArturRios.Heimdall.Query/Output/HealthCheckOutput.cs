using ArturRios.Mediator.Query;

namespace ArturRios.Heimdall.Query.Output;

/// <summary>
///     Detailed health check response (UC-30). Reports the aggregate general <see cref="Status" />
///     (<c>Healthy</c> when every verified service is up, <c>Unhealthy</c> otherwise — FR-HC-05) and
///     the per-service breakdown in <see cref="Services" /> (FR-HC-04). Adding a new verification
///     simply appends another entry to <see cref="Services" /> without changing this contract
///     (FR-HC-06).
/// </summary>
public class HealthCheckOutput : QueryOutput
{
    /// <summary>Aggregate general status — <c>Healthy</c> or <c>Unhealthy</c>.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>Status of each verified service.</summary>
    public IEnumerable<ServiceHealthOutput> Services { get; set; } = new List<ServiceHealthOutput>();
}
