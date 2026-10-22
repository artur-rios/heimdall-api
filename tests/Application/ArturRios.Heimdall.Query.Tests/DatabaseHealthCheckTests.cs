using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Query.HealthChecks;
using ArturRios.Output;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Mock;
using Microsoft.Extensions.Logging;

namespace ArturRios.Heimdall.Query.Tests;

// Unit tests for DatabaseHealthCheck (UC-30, FR-HC-03).
// Cover the reachable database (healthy) and the unreachable database (unhealthy, AF-30c) paths.
public class DatabaseHealthCheckTests
{
    [UnitFact]
    public async Task GivenReachableDatabase_WhenCheckingHealth_ThenReportsHealthy()
    {
        // Given a working repository, standing in for a reachable database
        var repository = new AsyncFakeRepository<Role, long>();
        var check = new DatabaseHealthCheck(repository, new RecordingLogger());

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
        var check = new DatabaseHealthCheck(new ThrowingRoleRepository(), new RecordingLogger());

        // When
        var healthy = await check.IsHealthyAsync();

        // Then
        Assert.False(healthy);
    }

    [UnitFact]
    public async Task GivenUnreachableDatabase_WhenCheckingHealth_ThenTheCauseIsLoggedAsAWarning()
    {
        // Given a repository whose query fails
        var logger = new RecordingLogger();
        var check = new DatabaseHealthCheck(new ThrowingRoleRepository(), logger);

        // When
        await check.IsHealthyAsync();

        // Then the swallowed exception is in the log, once, at Warning
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal("Database unreachable", entry.Exception?.Message);
    }

    [UnitFact]
    public async Task GivenReachableDatabase_WhenCheckingHealth_ThenNothingIsLogged()
    {
        // Given
        var logger = new RecordingLogger();
        var check = new DatabaseHealthCheck(new AsyncFakeRepository<Role, long>(), logger);

        // When
        await check.IsHealthyAsync();

        // Then
        Assert.Empty(logger.Entries);
    }

    // Keeps what the check logged — level and exception — so a test can assert the cause was recorded.
    private sealed class RecordingLogger : ILogger<DatabaseHealthCheck>
    {
        public List<(LogLevel Level, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add((logLevel, exception));
    }

    // Minimal read-only repository whose read throws — models a database that cannot be reached.
    private sealed class ThrowingRoleRepository : IAsyncReadOnlyRepository<Role, long>
    {
        public IQueryable<Role> Query() => throw new InvalidOperationException("Database unreachable");

        public Task<DataOutput<IEnumerable<Role>>> GetAllAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Database unreachable");

        public Task<DataOutput<Role?>> GetByIdAsync(long id, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Database unreachable");
    }
}
