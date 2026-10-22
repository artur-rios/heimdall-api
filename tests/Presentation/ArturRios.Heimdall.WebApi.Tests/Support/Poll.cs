namespace ArturRios.Heimdall.WebApi.Tests.Support;

/// <summary>
///     Waits for something a background worker does, by looking again at short intervals until a
///     deadline, rather than sleeping for a time somebody guessed.
/// </summary>
/// <remarks>
///     A fixed sleep is too long on a fast machine and too short on a slow one; polling returns the
///     moment the condition holds and fails with a message naming what never happened, not with a
///     later assertion that only shows its symptom.
/// </remarks>
public static class Poll
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(20);

    /// <summary>Returns once <paramref name="condition" /> holds; fails with <paramref name="timeoutMessage" /> if it never does.</summary>
    public static async Task UntilAsync(
        Func<Task<bool>> condition, string timeoutMessage, TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var deadline = DateTime.UtcNow + (timeout ?? DefaultTimeout);

        while (!await condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException(timeoutMessage);
            }

            await Task.Delay(Interval, cancellationToken);
        }
    }

    /// <inheritdoc cref="UntilAsync(Func{Task{bool}}, string, TimeSpan?, CancellationToken)" />
    public static Task UntilAsync(Func<bool> condition, string timeoutMessage, TimeSpan? timeout = null,
        CancellationToken cancellationToken = default) =>
        UntilAsync(() => Task.FromResult(condition()), timeoutMessage, timeout, cancellationToken);
}
