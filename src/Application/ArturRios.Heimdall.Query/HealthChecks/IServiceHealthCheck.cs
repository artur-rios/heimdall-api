namespace ArturRios.Heimdall.Query.HealthChecks;

/// <summary>
///     A single verifiable dependency of the API (UC-30, FR-HC-04/06). Each implementation reports
///     its own name, whether the API can work without it, and how it is doing. The internal health
///     report folds every registered check into the aggregate status, so a new dependency (cache,
///     external provider, …) is added by registering another implementation, with no change to the
///     report's shape.
/// </summary>
public interface IServiceHealthCheck
{
    /// <summary>Human-readable name of the verified service (e.g. <c>Database</c>).</summary>
    string ServiceName { get; }

    /// <summary>
    ///     Whether the API is down without this service. A required service that is not
    ///     <see cref="HealthStatuses.Healthy" /> makes the report <c>Unhealthy</c>; any other only makes it
    ///     <c>Degraded</c>.
    /// </summary>
    bool IsRequired { get; }

    /// <summary>
    ///     Asks the service how it is doing. A failure to reach it may be thrown: the report turns the
    ///     exception into a reason word, so no exception text can reach the report.
    /// </summary>
    Task<ServiceHealthResult> CheckAsync(CancellationToken cancellationToken);
}

/// <summary>What one check found.</summary>
/// <param name="Status">One of <see cref="HealthStatuses" />.</param>
/// <param name="Reason">
///     Why it is not healthy, in the fixed vocabulary of the report and never free text: the report is
///     readable by every container on the internal networks.
/// </param>
public sealed record ServiceHealthResult(string Status, string? Reason = null);
