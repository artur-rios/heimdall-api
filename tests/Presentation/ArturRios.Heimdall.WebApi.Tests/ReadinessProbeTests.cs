using System.Net;
using ArturRios.Configuration.Enums;
using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Query.HealthChecks;
using ArturRios.Heimdall.WebApi.Controllers;
using ArturRios.Heimdall.WebApi.Monitoring;
using ArturRios.Mediator.Query;
using Microsoft.Extensions.DependencyInjection;
using ArturRios.Heimdall.WebApi.Tests.Support;
using ArturRios.Output;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Functional;
using ArturRios.Util.Test.Mock;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArturRios.Heimdall.WebApi.Tests;

/// <summary>
///     The readiness probe (#215) answers 200 while the database does and 503 when it does not, with
///     only the bare status in the body.
/// </summary>
[Collection(nameof(FunctionalCollection))]
public class ReadinessProbeTests() : WebApiTest<Program>(EnvironmentType.Local)
{
    [FunctionalTheory]
    [InlineData("/api/healthcheck/ready")]
    [InlineData("/healthcheck/ready")]
    public async Task GivenTheDatabaseIsUp_WhenReadinessIsCalledWithoutAToken_ThenOkWithTheBareStatus(string route)
    {
        // Given / When
        var output = await Gateway.GetAsync<DataOutput<string>>(route);

        // Then
        Assert.Equal(HttpStatusCode.OK, output.StatusCode);
        Assert.Equal(HealthStatuses.Healthy, output.Body?.Data);
    }
}

/// <summary>The 503 half of the readiness probe, without booting the API.</summary>
public class ReadinessProbeUnavailableTests
{
    [UnitFact]
    public async Task GivenTheDatabaseIsDown_WhenReadinessIsCalled_ThenServiceUnavailableWithTheBareStatus()
    {
        // Given a database check whose read fails; the mediator serves the other actions and is not used here
        var check = new DatabaseHealthCheck(new ThrowingRoleRepository(), NullLogger<DatabaseHealthCheck>.Instance);
        var provider = new ServiceCollection().BuildServiceProvider();
        var mediator = new QueryMediator(provider.GetRequiredService<IServiceScopeFactory>());
        var controller = new HealthCheckController(mediator, check, new ReadinessCache(TimeProvider.System));

        // When
        var result = await controller.Ready();

        // Then
        var response = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal((int)HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = Assert.IsType<DataOutput<string?>>(response.Value);
        Assert.Equal(HealthStatuses.Unhealthy, body.Data);
        Assert.Empty(body.Errors);
    }

    private sealed class ThrowingRoleRepository : IAsyncReadOnlyRepository<Role, long>
    {
        public IQueryable<Role> Query() => throw new InvalidOperationException("Database unreachable");

        public Task<DataOutput<IEnumerable<Role>>> GetAllAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Database unreachable");

        public Task<DataOutput<Role?>> GetByIdAsync(long id, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Database unreachable");
    }
}
