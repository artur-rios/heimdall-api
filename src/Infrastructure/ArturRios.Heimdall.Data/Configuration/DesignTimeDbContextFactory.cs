using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArturRios.Heimdall.Data.Configuration;

/// <summary>
///     Builds an <see cref="AppDbContext" /> for the EF Core command-line tools, which have no
///     access to the application's dependency-injection container. The connection string comes from
///     <c>HEIMDALL_DATA_CONNECTIONSTRING</c>; <c>scripts/migrations.py</c> loads it from the
///     selected environment file before invoking <c>dotnet ef</c>. Diagnostics are disabled — design
///     time never needs them, and the tools may well be pointed at production.
/// </summary>
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    private const string ConnectionStringVariable = "HEIMDALL_DATA_CONNECTIONSTRING";

    public AppDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionStringVariable);

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"Environment variable '{ConnectionStringVariable}' is unset. Run scripts/migrations.py, " +
                "which loads it from the environment file you select, or set it manually before " +
                "invoking dotnet ef.");
        }

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        return new AppDbContext(options, NullLoggerFactory.Instance, DbContextDiagnosticsOptions.Disabled);
    }
}
