using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ArturRios.Heimdall.WebApi.Tests.Support;

/// <summary>
///     Holds a context at a precise point of its database conversation: the first command whose text starts
///     with <paramref name="prefix" /> waits, before it is sent, until the test calls <see cref="Resume" />.
///     <see cref="Reached" /> completes when it starts waiting.
/// </summary>
/// <remarks>
///     For the concurrency tests (#202, #203): a request held here has already done everything before that
///     command — inside its transaction, its locks taken and its guard asked — so a second request started now
///     really overlaps it, rather than running before or after it by luck. The wait is bounded, so a test whose
///     second request never lets it go fails instead of hanging.
/// </remarks>
public sealed class PauseBefore(string prefix) : DbCommandInterceptor
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _fired;

    /// <summary>Completes when the matching command starts waiting.</summary>
    public Task Reached => _reached.Task;

    /// <summary>Lets the held command go.</summary>
    public void Resume() => _resume.TrySetResult();

    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
        CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        await HoldIfMatchingAsync(command);
        return result;
    }

    public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
        CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        await HoldIfMatchingAsync(command);
        return result;
    }

    public override async ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command,
        CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
    {
        await HoldIfMatchingAsync(command);
        return result;
    }

    private async Task HoldIfMatchingAsync(DbCommand command)
    {
        if (!command.CommandText.TrimStart().StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            Interlocked.Exchange(ref _fired, 1) == 1)
        {
            return;
        }

        _reached.TrySetResult();
        await _resume.Task.WaitAsync(Bound);
    }
}
