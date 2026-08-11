using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Heimdall.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Heimdall.Query.HealthChecks;

/// <summary>
///     Verifies the database connection (UC-30, FR-HC-03) by issuing a trivial read through the
///     repository abstraction. If the query completes the connection is healthy; any failure (the
///     database being unreachable throws on execution) is treated as unhealthy rather than
///     propagated, so the detailed health check can still report the aggregate status (AF-30c).
/// </summary>
public class DatabaseHealthCheck(IAsyncReadOnlyRepository<Role> roleReader) : IServiceHealthCheck
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
        catch
        {
            return false;
        }
    }
}
