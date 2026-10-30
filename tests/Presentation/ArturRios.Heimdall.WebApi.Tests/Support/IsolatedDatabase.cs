using ArturRios.Heimdall.Data.Configuration;
using ArturRios.Heimdall.Data.Seeding;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace ArturRios.Heimdall.WebApi.Tests.Support;

/// <summary>
///     A database of its own in the suite's container, set up as <see cref="PostgresFixture" /> sets up the
///     shared one — the deployment script, the migrations as the owner, the script again, the seeder — and
///     dropped when disposed.
/// </summary>
/// <remarks>
///     For a test whose rule is about the whole system rather than about rows it seeds: NFR-32 counts every
///     active System Admin in the database, and the shared one holds the master user, the stand-ins and every
///     administrator other tests have seeded. Here the seeder's master user is the only one until the test adds
///     more.
/// </remarks>
public sealed class IsolatedDatabase : IAsyncDisposable
{
    private readonly PostgresFixture _db;

    private IsolatedDatabase(PostgresFixture db, string name)
    {
        _db = db;
        Name = name;
    }

    /// <summary>The database's name.</summary>
    public string Name { get; }

    /// <summary>A superuser's connection, for arranging and inspecting state.</summary>
    public string SuperuserConnectionString => _db.ConnectionStringFor("postgres", "postgres", Name);

    /// <summary>The owner's connection: what the migrations run as.</summary>
    private string OwnerConnectionString => On(_db.OwnerConnectionString);

    /// <summary>The runtime login's connection: what the API uses (NFR-29).</summary>
    public string RuntimeConnectionString => On(_db.RuntimeConnectionString);

    public static async Task<IsolatedDatabase> CreateAsync(PostgresFixture db)
    {
        var isolated = new IsolatedDatabase(db, $"heimdall_isolated_{Guid.NewGuid():N}");
        var variables = new Dictionary<string, string>
        {
            ["owner"] = PostgresFixture.OwnerRole, ["runtime"] = PostgresFixture.RuntimeRole, ["database"] = isolated.Name
        };

        try
        {
            await RunOrThrowAsync(db, variables);

            await using (var owner = PostgresFixture.CreateContext(isolated.OwnerConnectionString))
            {
                await owner.Database.MigrateAsync();
            }

            await RunOrThrowAsync(db, variables);

            await using var context = PostgresFixture.CreateContext(isolated.SuperuserConnectionString);
            await new DatabaseSeeder(context, MasterUserOptions.FromEnvironment(), NullLogger<DatabaseSeeder>.Instance)
                .SeedAsync();
        }
        catch
        {
            // Whatever was created is dropped, so a failed set-up leaves no database behind.
            await isolated.DisposeAsync();
            throw;
        }

        return isolated;
    }

    /// <summary>A context on this database as the superuser.</summary>
    public AppDbContext CreateContext() => PostgresFixture.CreateContext(SuperuserConnectionString);

    public async ValueTask DisposeAsync()
    {
        // Only this database's pools: the shared database's connections, and every other test's, are left
        // alone. The drop's FORCE ends any connection still open.
        string[] connectionStrings = [SuperuserConnectionString, OwnerConnectionString, RuntimeConnectionString];

        foreach (var connectionString in connectionStrings)
        {
            using var pooled = new NpgsqlConnection(connectionString);
            NpgsqlConnection.ClearPool(pooled);
        }

        await using var connection = new NpgsqlConnection(_db.ConnectionStringFor("postgres", "postgres", "postgres"));
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"DROP DATABASE IF EXISTS {Name} WITH (FORCE)", connection);
        await command.ExecuteNonQueryAsync();
    }

    private string On(string connectionString) =>
        new NpgsqlConnectionStringBuilder(connectionString) { Database = Name }.ConnectionString;

    private static async Task RunOrThrowAsync(PostgresFixture db, IReadOnlyDictionary<string, string> variables)
    {
        var result = await db.RunScriptAsync("new-deployment.sql", variables);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"new-deployment.sql exited with {result.ExitCode}: {result.Stderr}");
        }
    }
}
