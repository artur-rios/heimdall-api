namespace ArturRios.Heimdall.WebApi.Retention;

/// <summary>What one run of a <see cref="ScheduledPassService" /> pass did.</summary>
/// <param name="Summary">
///     The line to log: the outcome of a run that changed something, the errors of one that failed, or
///     <c>null</c> for a clean run with nothing worth saying.
/// </param>
/// <param name="Failed">
///     Whether the pass's command reported failure. A failed run is logged at Error with
///     <see cref="ScheduledPassService.FailureMarker" /> and does not count as a success.
/// </param>
/// <param name="RowsProcessed">The rows the pass reports having changed, for the rows-processed counter.</param>
public sealed record ScheduledPassResult(string? Summary, bool Failed = false, long RowsProcessed = 0)
{
    /// <summary>A run that did nothing worth reporting.</summary>
    public static ScheduledPassResult Quiet => new(null, false);

    /// <summary>A failed run, described by the command's errors.</summary>
    public static ScheduledPassResult Failure(string summary, IEnumerable<string> errors) =>
        new($"{summary} failed: {string.Join("; ", errors)}", true);
}
