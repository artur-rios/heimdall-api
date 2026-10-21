using ArturRios.Heimdall.Data.Configuration;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ArturRios.Heimdall.WebApi.Tests.Support;

/// <summary>
///     What a concurrency test needs to know about a second request while the first is held
///     (<see cref="PauseBefore" />): which server process its context talks to, and when that process is
///     waiting on a lock.
/// </summary>
public static class LockWaits
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    /// <summary>
    ///     Opens the context's connection, which it then keeps for every command, and returns its server
    ///     process id.
    /// </summary>
    public static async Task<int> OpenAndGetPidAsync(AppDbContext context)
    {
        await context.Database.OpenConnectionAsync();
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT pg_backend_pid()";
        return (int)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>
    ///     Waits until the server process <paramref name="pid" /> is waiting on a lock — a row lock or an
    ///     advisory one — or until <paramref name="request" /> has finished without needing to. Watches,
    ///     rather than sleeps a guess: <c>pg_stat_activity</c> says when the process is blocked.
    /// </summary>
    public static async Task WaitUntilBlockedOrDoneAsync(PostgresFixture db, int pid, Task request)
    {
        await using var connection = new NpgsqlConnection(db.ConnectionString);
        await connection.OpenAsync();
        var deadline = DateTime.UtcNow + Bound;

        while (!request.IsCompleted)
        {
            await using var command = new NpgsqlCommand(
                "SELECT wait_event_type FROM pg_stat_activity WHERE pid = @pid", connection);
            command.Parameters.AddWithValue("pid", pid);

            if (await command.ExecuteScalarAsync() is "Lock")
            {
                return;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Process {pid} neither blocked on a lock nor finished.");
            }

            await Task.Delay(20);
        }
    }
}
