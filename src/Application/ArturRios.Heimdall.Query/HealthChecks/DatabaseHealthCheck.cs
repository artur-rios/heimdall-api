using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Heimdall.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ArturRios.Heimdall.Query.HealthChecks;

/// <summary>
///     Verifies the database connection (UC-30, FR-HC-03) by issuing a trivial read through the
///     repository abstraction. The database is the one service the API cannot work without, so it is
///     required: while it is down the report is <c>Unhealthy</c> (AF-30c).
/// </summary>
/// <remarks>
///     The readiness probe's <see cref="IsHealthyAsync" /> swallows the exception so the endpoint keeps
///     answering, and logs it at Warning: the response says only "Unhealthy", and without the cause in the
///     log nobody reading it has a reason to go on.
/// </remarks>
public class DatabaseHealthCheck(
    IAsyncReadOnlyRepository<Role, long> roleReader,
    ILogger<DatabaseHealthCheck> logger) : IServiceHealthCheck
{
    public string ServiceName => "Database";

    public bool IsRequired => true;

    /// <inheritdoc />
    /// <remarks>
    ///     Leaves the exception to the report, which logs a reason word once per change. Logging it here
    ///     as well would put a stack trace in the log on every run while the database is down.
    /// </remarks>
    public async Task<ServiceHealthResult> CheckAsync(CancellationToken cancellationToken)
    {
        await roleReader.Query().AnyAsync(cancellationToken);

        return new ServiceHealthResult(HealthStatuses.Healthy);
    }

    public async Task<bool> IsHealthyAsync()
    {
        try
        {
            // A minimal round-trip to the database — succeeds only if the connection is usable.
            await roleReader.Query().AnyAsync();

            return true;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "The database health check failed");

            return false;
        }
    }
}
