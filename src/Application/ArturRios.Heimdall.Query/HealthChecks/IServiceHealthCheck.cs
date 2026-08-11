namespace ArturRios.Heimdall.Query.HealthChecks;

/// <summary>
///     A single verifiable dependency of the API (UC-30, FR-HC-04/06). Each implementation reports
///     its own name and whether it is currently reachable/healthy. The detailed health check folds
///     every registered check into the aggregate status, so a new dependency (cache, email, external
///     provider, …) is added by registering another implementation — no change to the response
///     contract or to <see cref="Handlers.GetDetailedHealthQueryHandler" />.
/// </summary>
public interface IServiceHealthCheck
{
    /// <summary>Human-readable name of the verified service (e.g. <c>Database</c>).</summary>
    string ServiceName { get; }

    /// <summary>Returns <c>true</c> when the service is reachable/healthy, <c>false</c> otherwise.</summary>
    Task<bool> IsHealthyAsync();
}
