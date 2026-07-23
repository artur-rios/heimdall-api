using ArturRios.IdentityManager.Data.Configuration;
using ArturRios.IdentityManager.Data.Seeding;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.PostgreSql;

namespace ArturRios.IdentityManager.WebApi.Tests.Support;

/// <summary>
///     Starts a throwaway PostgreSQL container once for the whole functional test suite, applies the
///     EF migrations to it, and exposes its connection string, so functional tests run end-to-end
///     against a real database that closely matches production. Shared via
///     <see cref="FunctionalCollection" /> so the container is created once, not per test class.
///     See docs/Testing Specification Document.md §7.2.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private const string ConnectionStringVariable = "IDENTITY_MANAGER_DATA_CONNECTIONSTRING";
    private const string DatabaseTypeVariable = "IDENTITY_MANAGER_DATA_DATABASETYPE";
    private const string TokenSecretVariable = "IDENTITY_MANAGER_AUTH_TOKEN_SECRET";
    private const string TokenIssuerVariable = "IDENTITY_MANAGER_AUTH_TOKEN_ISSUER";
    private const string TokenAudienceVariable = "IDENTITY_MANAGER_AUTH_TOKEN_AUDIENCE";

    /// <summary>E-mail of the master system administrator the API seeds into the container.</summary>
    public const string MasterUserEmail = "master@identity-manager.test";

    /// <summary>Password the master system administrator is seeded with.</summary>
    public const string MasterUserPassword = "Str0ng-Master-Pass!";

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

        // Startup refuses to boot without a signing secret, since an empty one makes every request
        // fail inside the token validator. This value is for tests only.
        Environment.SetEnvironmentVariable(TokenSecretVariable, "functional-tests-signing-secret-key");
        Environment.SetEnvironmentVariable(TokenIssuerVariable, "identity-manager-tests");
        Environment.SetEnvironmentVariable(TokenAudienceVariable, "identity-manager-tests");

        // The seeder refuses to start without a configured master user.
        Environment.SetEnvironmentVariable(MasterUserOptions.NameVariable, "Master User");
        Environment.SetEnvironmentVariable(MasterUserOptions.EmailVariable, MasterUserEmail);
        Environment.SetEnvironmentVariable(MasterUserOptions.PasswordVariable, MasterUserPassword);

        // The API refuses to start against a schema with pending migrations, so apply them here.
        await using var context = CreateContext();

        await context.Database.MigrateAsync();
    }

    /// <summary>
    ///     Creates a context bound to the container, for tests that assert on database state
    ///     directly rather than through the API.
    /// </summary>
    public AppDbContext CreateContext() => new(
        new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(ConnectionString).Options,
        NullLoggerFactory.Instance,
        DbContextDiagnosticsOptions.Disabled);

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();
}
