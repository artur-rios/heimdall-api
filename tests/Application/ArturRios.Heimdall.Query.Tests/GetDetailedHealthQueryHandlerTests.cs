using ArturRios.Heimdall.Query.Handlers;
using ArturRios.Heimdall.Query.HealthChecks;
using ArturRios.Heimdall.Query.Input;
using ArturRios.Util.Test.Attributes;

namespace ArturRios.Heimdall.Query.Tests;

// Unit tests for GetDetailedHealthQueryHandler (UC-30, FR-HC-04/05/06).
// Cover the aggregate rule: all services up => Healthy; any service down => Unhealthy (AF-30c);
// and the extensibility contract (per-service breakdown, arbitrary number of checks).
public class GetDetailedHealthQueryHandlerTests
{
    // Hand-written stub — no mocking framework, matching this project's conventions.
    private sealed class StubHealthCheck(string name, bool healthy) : IServiceHealthCheck
    {
        public string ServiceName => name;
        public Task<bool> IsHealthyAsync() => Task.FromResult(healthy);
    }

    [UnitFact]
    public async Task GivenAllServicesHealthy_WhenHandlingDetailedHealth_ThenAggregateIsHealthy()
    {
        // Given every registered check reports healthy
        var handler = new GetDetailedHealthQueryHandler([new StubHealthCheck("Database", healthy: true)]);

        // When
        var output = await handler.HandleAsync(new DetailedHealthQuery());

        // Then
        Assert.True(output.Success);
        Assert.NotNull(output.Data);
        Assert.Equal(HealthStatuses.Healthy, output.Data!.Status);

        var service = Assert.Single(output.Data.Services);
        Assert.Equal("Database", service.Name);
        Assert.Equal(HealthStatuses.Healthy, service.Status);
    }

    [UnitFact]
    public async Task GivenOneServiceUnhealthy_WhenHandlingDetailedHealth_ThenAggregateIsUnhealthy()
    {
        // Given one of several checks is down
        var handler = new GetDetailedHealthQueryHandler([
            new StubHealthCheck("Database", healthy: true),
            new StubHealthCheck("Cache", healthy: false)
        ]);

        // When
        var output = await handler.HandleAsync(new DetailedHealthQuery());

        // Then the aggregate is Unhealthy while each service keeps its own status
        Assert.Equal(HealthStatuses.Unhealthy, output.Data!.Status);
        Assert.Equal(HealthStatuses.Healthy, output.Data.Services.First(s => s.Name == "Database").Status);
        Assert.Equal(HealthStatuses.Unhealthy, output.Data.Services.First(s => s.Name == "Cache").Status);
    }

    [UnitFact]
    public async Task GivenNoServicesRegistered_WhenHandlingDetailedHealth_ThenAggregateIsHealthy()
    {
        // Given no checks are registered, the aggregate is vacuously Healthy (guards the rule)
        var handler = new GetDetailedHealthQueryHandler([]);

        // When
        var output = await handler.HandleAsync(new DetailedHealthQuery());

        // Then
        Assert.Equal(HealthStatuses.Healthy, output.Data!.Status);
        Assert.Empty(output.Data.Services);
    }
}
