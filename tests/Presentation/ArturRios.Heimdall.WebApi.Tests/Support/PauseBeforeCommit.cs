using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ArturRios.Heimdall.WebApi.Tests.Support;

/// <summary>
///     Holds a context's first transaction commit until the test calls <see cref="Resume" />, so whatever the
///     transaction locked stays locked while a second request runs into it. <see cref="Reached" /> completes
///     when the commit starts waiting. Bounded, as <see cref="PauseBefore" /> is.
/// </summary>
public sealed class PauseBeforeCommit : DbTransactionInterceptor
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _fired;

    /// <summary>Completes when the commit starts waiting.</summary>
    public Task Reached => _reached.Task;

    /// <summary>Lets the held commit go.</summary>
    public void Resume() => _resume.TrySetResult();

    public override async ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
        TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _fired, 1) == 0)
        {
            _reached.TrySetResult();
            await _resume.Task.WaitAsync(Bound);
        }

        return result;
    }
}
