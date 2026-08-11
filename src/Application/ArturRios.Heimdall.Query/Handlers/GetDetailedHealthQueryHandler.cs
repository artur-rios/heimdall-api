using ArturRios.Heimdall.Query.HealthChecks;
using ArturRios.Heimdall.Query.Input;
using ArturRios.Heimdall.Query.Output;
using ArturRios.Mediator.Query.Interfaces;
using ArturRios.Output;

namespace ArturRios.Heimdall.Query.Handlers;

/// <summary>
///     Handles <see cref="DetailedHealthQuery" /> (UC-30, FR-HC-02…06): runs every registered
///     <see cref="IServiceHealthCheck" />, reports each service's status, and computes the aggregate
///     general status — <c>Healthy</c> only when all services are up, <c>Unhealthy</c> if any is down
///     (FR-HC-05, AF-30c). The set of checks is injected, so new verifications participate without
///     changing this handler.
/// </summary>
public class GetDetailedHealthQueryHandler(IEnumerable<IServiceHealthCheck> healthChecks)
    : IQueryHandlerAsync<DetailedHealthQuery, HealthCheckOutput>
{
    public async Task<DataOutput<HealthCheckOutput?>> HandleAsync(DetailedHealthQuery query)
    {
        var services = new List<ServiceHealthOutput>();

        foreach (var check in healthChecks)
        {
            var healthy = await check.IsHealthyAsync();

            services.Add(new ServiceHealthOutput
            {
                Name = check.ServiceName,
                Status = healthy ? HealthStatuses.Healthy : HealthStatuses.Unhealthy
            });
        }

        // Aggregate: any unhealthy service makes the whole API unhealthy (FR-HC-05).
        var aggregate = services.TrueForAll(service => service.Status == HealthStatuses.Healthy)
            ? HealthStatuses.Healthy
            : HealthStatuses.Unhealthy;

        var result = new HealthCheckOutput
        {
            Status = aggregate,
            Services = services
        };

        return DataOutput<HealthCheckOutput?>.New.WithData(result);
    }
}
