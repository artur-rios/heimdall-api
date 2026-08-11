using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Query.HealthChecks;
using ArturRios.Output;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Mock;

namespace ArturRios.Heimdall.Query.Tests;

// Unit tests for DatabaseHealthCheck (UC-30, FR-HC-03).
// Cover the reachable database (healthy) and the unreachable database (unhealthy, AF-30c) paths.
public class DatabaseHealthCheckTests
{
    [UnitFact]
    public async Task GivenReachableDatabase_WhenCheckingHealth_ThenReportsHealthy()
    {
        // Given a working repository, standing in for a reachable database
        var repository = new AsyncFakeRepository<Role>();
        var check = new DatabaseHealthCheck(repository);

        // When
        var healthy = await check.IsHealthyAsync();

        // Then
        Assert.True(healthy);
        Assert.Equal("Database", check.ServiceName);
    }

    [UnitFact]
    public async Task GivenUnreachableDatabase_WhenCheckingHealth_ThenReportsUnhealthy()
    {
        // Given a repository whose query fails, standing in for a database that is down
        var check = new DatabaseHealthCheck(new ThrowingRoleRepository());

        // When
        var healthy = await check.IsHealthyAsync();

        // Then
        Assert.False(healthy);
    }

    // Minimal read-only repository whose read throws — models a database that cannot be reached.
    private sealed class ThrowingRoleRepository : IAsyncReadOnlyRepository<Role>
    {
        public IQueryable<Role> Query() => throw new InvalidOperationException("Database unreachable");

        public Task<DataOutput<IEnumerable<Role>>> GetAllAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Database unreachable");

        public Task<DataOutput<Role?>> GetByIdAsync(long id, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Database unreachable");
    }
}
