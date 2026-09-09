using ArturRios.Heimdall.Query.Handlers;
using ArturRios.Heimdall.Query.Input;
using ArturRios.Heimdall.Query.Output;
using ArturRios.Heimdall.Shared.Retention;
using ArturRios.Mediator.Query;
using ArturRios.Heimdall.WebApi.Retention;

namespace ArturRios.Heimdall.WebApi.Monitoring;

/// <summary>
///     Reads the security signals the system already produces and writes the ones worth acting on
///     to the log, at warning level, with a stable marker an alerting rule can match (NFR-26).
/// </summary>
/// <remarks>
///     <para>
///         <b>The log is the destination, deliberately.</b> This API has no opinion about where an
///         operator's alerts go — a mail relay, a pager, a SIEM — and building an integration for
///         one would be guessing. What it can do is make the signal unmistakable and machine-matchable
///         where the operator is already collecting: the logs, which #98 gave a retention period and
///         which ship off the host.
///     </para>
///     <para>
///         <b>Every signal is logged on every run it persists.</b> There is no suppression or
///         deduplication, which would be the obvious refinement and is the wrong one here: an
///         attack that continues is more interesting on its tenth tick than its first, and a
///         detector that goes quiet while the thing it detects is still happening is worse than no
///         detector.
///     </para>
/// </remarks>
public class SecurityMonitoringService(
    IServiceScopeFactory scopeFactory,
    DataRetentionOptions options,
    ILogger<SecurityMonitoringService> logger)
    : ScheduledPassService(scopeFactory, options.MonitoringWindow, logger)
{
    /// <summary>
    ///     The marker every signal line carries. Stable, greppable, and the thing an alerting rule
    ///     should match on rather than the prose, which is written for a human reading afterwards.
    /// </summary>
    public const string Marker = "SECURITY_SIGNAL";

    protected override string StartupDescription =>
        $"Security signal detection scheduled every {options.MonitoringWindow}, alerting at "
        + $"{options.RefusalThreshold} refusals by one identity or {options.LockoutThreshold} "
        + "accounts locked out at once";

    /// <remarks>
    ///     Resolves the query mediator rather than the command one every other pass uses: this pass
    ///     reads, and making it a command would put an audit entry on every tick saying "looked, saw
    ///     nothing", burying the writes NFR-09 exists to record.
    /// </remarks>
    protected override async Task<string?> RunAsync(IServiceProvider scope)
    {
        var result = await scope.GetRequiredService<QueryMediator>()
            .ExecuteQueryAsync<DetectSecuritySignalsQuery, SecuritySignalsOutput>(
                new DetectSecuritySignalsQuery());

        if (!result.Success)
        {
            return $"Security signal detection failed: {string.Join("; ", result.Errors)}";
        }

        var signals = result.Data?.Signals.ToList() ?? [];

        foreach (var signal in signals)
        {
            logger.LogWarning(
                "{Marker} {Kind}: {Description} (count {Count}, actor {ActorId})",
                Marker, signal.Kind, signal.Description, signal.Count,
                signal.ActorId?.ToString() ?? "n/a");
        }

        // Nothing extra for the caller to log: each signal has already been written above, and a
        // run that found nothing is not news.
        return null;
    }
}
