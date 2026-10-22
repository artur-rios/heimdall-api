using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

namespace ArturRios.Heimdall.WebApi.Retention;

/// <summary>
///     The instruments every scheduled pass reports through, labelled by <c>pass</c>, so an operator can
///     alert on "no successful run in N hours" instead of relying on a log line being noticed (#214).
/// </summary>
/// <remarks>
///     Registered on the meter <see cref="MeterName" />, which <c>MetricsConfiguration</c> subscribes the
///     exporter to. The instruments exist whether or not metrics are enabled; with nothing listening
///     they cost a branch.
/// </remarks>
public static class ScheduledPassMetrics
{
    /// <summary>The meter the exporter is told to collect.</summary>
    public const string MeterName = "ArturRios.Heimdall.ScheduledPasses";

    private static readonly Meter Meter = new(MeterName);

    private static readonly Counter<long> Rows = Meter.CreateCounter<long>(
        "heimdall.scheduled_pass.rows_processed",
        description: "Rows a scheduled pass has changed, by pass.");

    private static readonly ConcurrentDictionary<string, long> LastSuccess = new();

    // Held only so the instrument is not collected; the callback reads LastSuccess.
    private static readonly ObservableGauge<long> LastSuccessGauge = Meter.CreateObservableGauge(
        "heimdall.scheduled_pass.last_success_timestamp",
        () => LastSuccess.Select(entry => new Measurement<long>(entry.Value, new KeyValuePair<string, object?>("pass", entry.Key))),
        description: "Unix time in seconds of the last run of a scheduled pass that completed without failure, by pass.");

    /// <summary>Adds the rows a run reported to the pass's counter.</summary>
    public static void RowsProcessed(string pass, long rows)
    {
        if (rows > 0)
        {
            Rows.Add(rows, new KeyValuePair<string, object?>("pass", pass));
        }
    }

    /// <summary>Records now as the pass's last successful run.</summary>
    public static void Succeeded(string pass) => LastSuccess[pass] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    /// <summary>The pass's last successful run as unix seconds, or <c>null</c> if it has not succeeded.</summary>
    public static long? LastSuccessOf(string pass) => LastSuccess.TryGetValue(pass, out var at) ? at : null;
}
