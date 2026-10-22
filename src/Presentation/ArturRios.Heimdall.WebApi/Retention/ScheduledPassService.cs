using ArturRios.Mediator.Command;

namespace ArturRios.Heimdall.WebApi.Retention;

/// <summary>
///     Runs one scheduled pass on an interval. Subclasses supply the pass; everything about
///     <em>when</em> it runs, and what happens when it fails, lives here.
/// </summary>
/// <remarks>
///     <para>
///         The first tick is one whole interval after start-up, which is deliberate: it keeps the
///         passes clear of migrations and seeding, and it means a container that restarts repeatedly
///         never turns start-up into write pressure on the tables the login path uses.
///     </para>
///     <para>
///         A failed run — one that returns a failure or throws — is logged at Error with
///         <see cref="FailureMarker" /> and the loop continues. These are background tasks, not part of
///         serving a request — a database blip must not take the service down, and the next tick
///         retries whatever was missed, because a row past its retention period stays past it.
///     </para>
///     <para>
///         Every instance runs its own copy of every pass, which is what NFR-06 asks for: an
///         instance that assumed another was doing the work would stop doing it the moment it ran
///         alone. Each pass documents why overlapping runs need no coordination.
///     </para>
/// </remarks>
public abstract class ScheduledPassService(
    IServiceScopeFactory scopeFactory,
    TimeSpan interval,
    ILogger logger) : BackgroundService
{
    /// <summary>
    ///     The marker a failed pass's log line carries, at Error level. Stable and greppable: an alert
    ///     should match on it rather than on the prose, which is written for a human and will change.
    /// </summary>
    public const string FailureMarker = "SCHEDULED_PASS_FAILED";

    /// <summary>What the service writes at start-up to say what it will do and how often.</summary>
    protected abstract string StartupDescription { get; }

    /// <summary>
    ///     The pass's stable name: the <c>pass</c> label on its metrics and the name beside the failure
    ///     marker. Lower-case and hyphenated, and not to be changed once an alert depends on it.
    /// </summary>
    public abstract string PassName { get; }

    /// <summary>
    ///     Dispatches the pass and returns what it did — a summary for the log (or <c>null</c> when
    ///     there is nothing worth saying, so an hourly no-op does not bury the runs that matter), whether
    ///     it failed, and how many rows it processed.
    /// </summary>
    /// <param name="scope">
    ///     A fresh dependency-injection scope. The pass resolves what it needs from it — most
    ///     resolve <see cref="CommandMediator" />, the security detector a <c>QueryMediator</c>,
    ///     because it reads rather than writes.
    /// </param>
    protected abstract Task<ScheduledPassResult> RunAsync(IServiceProvider scope);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("{Description}", StartupDescription);

        using var timer = new PeriodicTimer(interval);

        while (await SafeWaitForNextTickAsync(timer, stoppingToken))
        {
            await RunOnceAsync();
        }
    }

    /// <summary>
    ///     Waits for the next tick, reporting shutdown as a stop rather than letting the
    ///     cancellation surface as an exception the host logs as a fault.
    /// </summary>
    private static async Task<bool> SafeWaitForNextTickAsync(PeriodicTimer timer, CancellationToken stoppingToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private async Task RunOnceAsync()
    {
        try
        {
            // The mediator is scoped, and so is the DbContext behind it, so the run needs a scope of
            // its own — this service is a singleton and has none.
            using var scope = scopeFactory.CreateScope();

            var result = await RunAsync(scope.ServiceProvider);

            ScheduledPassMetrics.RowsProcessed(PassName, result.RowsProcessed);

            if (result.Failed)
            {
                // Error, not Information: an alert on Warning or above has to fire when a retention
                // obligation (NFR-19..21) stops being met, and the metric below deliberately stays put.
                logger.LogError("{Marker} {Pass}: {Summary}", FailureMarker, PassName, result.Summary);
                return;
            }

            ScheduledPassMetrics.Succeeded(PassName);

            if (result.Summary is not null)
            {
                logger.LogInformation("{Summary}", result.Summary);
            }
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "{Marker} {Pass}: the pass threw", FailureMarker, PassName);
        }
    }
}
