using ArturRios.Heimdall.Data.Configuration;
using ArturRios.Heimdall.Data.Seeding;
using ArturRios.Heimdall.WebApi.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.PostgreSql;

namespace ArturRios.Heimdall.WebApi.Tests.Support;

/// <summary>
///     Starts a throwaway PostgreSQL container once for the whole functional test suite, applies the
///     EF migrations to it, and exposes its connection string, so functional tests run end-to-end
///     against a real database that closely matches production. Shared via
///     <see cref="FunctionalCollection" /> so the container is created once, not per test class.
///     See docs/Testing Specification Document.md §7.2.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private const string ConnectionStringVariable = "HEIMDALL_DATA_CONNECTIONSTRING";
    private const string DatabaseTypeVariable = "HEIMDALL_DATA_DATABASETYPE";
    private const string TokenSecretVariable = "HEIMDALL_AUTH_TOKEN_SECRET";
    private const string TokenIssuerVariable = "HEIMDALL_AUTH_TOKEN_ISSUER";
    private const string TokenAudienceVariable = "HEIMDALL_AUTH_TOKEN_AUDIENCE";

    /// <summary>E-mail of the master system administrator the API seeds into the container.</summary>
    public const string MasterUserEmail = "master@heimdall.test";

    /// <summary>Password the master system administrator is seeded with.</summary>
    public const string MasterUserPassword = "Str0ng-Master-Pass!";

    /// <summary>
    ///     Secret the suite's stand-in Google ID tokens are signed with (UC-25). Long enough for
    ///     HMAC-SHA256, which refuses a key shorter than its 256-bit output.
    /// </summary>
    public const string GoogleTestSigningSecret = "functional-tests-google-id-token-signing-secret";

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
        Environment.SetEnvironmentVariable(TokenIssuerVariable, "heimdall-tests");
        Environment.SetEnvironmentVariable(TokenAudienceVariable, "heimdall-tests");

        // UC-25 verifies Google ID tokens. Publishing this secret makes the host under test resolve
        // LocalGoogleIdTokenVerifier instead of the real one, so the suite exercises sign-up,
        // sign-in, AF-25c, and AF-25d — all of which sit behind verification — without reaching
        // Google. Tokens still have to be validly signed; TestGoogleTokens mints them with this same
        // secret. Never set outside the suite: Startup ignores it in Production and no .env file
        // carries it.
        Environment.SetEnvironmentVariable(
            GoogleSignInOptions.TestSigningSecretVariable, GoogleTestSigningSecret);

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
