using Testcontainers.PostgreSql;

namespace ArturRios.IdentityManager.WebApi.Tests.Support;

/// <summary>
///     Starts a throwaway PostgreSQL container once for the whole functional test suite and exposes
///     its connection string, so functional tests run end-to-end against a real database that closely
///     matches production. Shared via <see cref="FunctionalCollection" /> so the container is created
///     once, not per test class. See docs/Testing Specification Document.md §7.2.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private const string ConnectionStringVariable = "IDENTITY_MANAGER_DATA_CONNECTIONSTRING";
    private const string DatabaseTypeVariable = "IDENTITY_MANAGER_DATA_DATABASETYPE";

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16-alpine")
        .Build();

    /// <summary>The connection string of the running container's database.</summary>
    public string ConnectionString => _container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        // Point the API under test at the container instead of a developer's local database.
        Environment.SetEnvironmentVariable(ConnectionStringVariable, ConnectionString);
        Environment.SetEnvironmentVariable(DatabaseTypeVariable, "PostgreSql");

        // TODO: create the schema on the container before the first test runs.
        // There are no EF migrations in the repository yet; once the migration strategy is decided,
        // apply migrations (or AppDbContext.Database.EnsureCreated()) against ConnectionString here.
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();
}
