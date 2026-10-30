using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Heimdall.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ArturRios.Heimdall.Query.HealthChecks;

/// <summary>
///     Verifies the database connection (UC-30, FR-HC-03) by issuing a trivial read through the
///     repository abstraction. If the query completes the connection is healthy; any failure (the
///     database being unreachable throws on execution) is treated as unhealthy rather than
///     propagated, so the detailed health check can still report the aggregate status (AF-30c).
/// </summary>
/// <remarks>
///     The exception is swallowed so the endpoint keeps answering, but it is logged at Warning: the
///     response says only "Unhealthy", and without the cause in the log nobody reading it has a
///     reason to go on.
/// </remarks>
public class DatabaseHealthCheck(
    IAsyncReadOnlyRepository<Role, long> roleReader,
    ILogger<DatabaseHealthCheck> logger) : IServiceHealthCheck
{
    public string ServiceName => "Database";

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
