namespace ArturRios.Heimdall.WebApi.Monitoring;

/// <summary>
///     Holds the readiness answer for <see cref="Window">a few seconds</see>, so the anonymous probe costs
///     the database one read per window however often it is called (#215).
/// </summary>
/// <remarks>
///     A singleton. Callers that arrive while a check is in flight, or within the window after it, share
///     its result, so the window expiring does not release a herd of reads at once. The unhealthy answer is
///     held too: while the database is down, a flood of probes writes no further read, stack trace or
///     Warning until the window ends. Five seconds is shorter than the Compose probe's interval, so the
///     probe still sees every change.
/// </remarks>
public class ReadinessCache(TimeProvider clock)
{
    /// <summary>How long an answer is reused.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(5);

    private readonly object _gate = new();
    private Task<bool>? _current;
    private DateTimeOffset _startedAt;

    /// <summary>The cached answer, or the result of <paramref name="check" /> when there is none current.</summary>
    public Task<bool> IsReadyAsync(Func<Task<bool>> check)
    {
        lock (_gate)
        {
            var now = clock.GetUtcNow();

            if (_current is null || now - _startedAt >= Window)
            {
                _current = check();
                _startedAt = now;
            }

            return _current;
        }
    }
}
