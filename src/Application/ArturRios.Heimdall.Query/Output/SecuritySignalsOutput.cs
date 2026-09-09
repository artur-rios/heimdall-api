using ArturRios.Mediator.Query;

namespace ArturRios.Heimdall.Query.Output;

/// <summary>What the last detection run found (NFR-26).</summary>
public class SecuritySignalsOutput : QueryOutput
{
    /// <summary>When the run happened.</summary>
    public DateTime DetectedAt { get; set; }

    /// <summary>How far back it looked.</summary>
    public TimeSpan Window { get; set; }

    /// <summary>The signals worth telling somebody about. Empty on most runs.</summary>
    public IEnumerable<SecuritySignal> Signals { get; set; } = [];
}

/// <summary>One thing worth telling somebody about.</summary>
public class SecuritySignal
{
    /// <summary>
    ///     A stable identifier for the kind of signal, so an alerting rule can match on it without
    ///     parsing prose.
    /// </summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>What was observed, in words.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>How many occurrences were counted.</summary>
    public int Count { get; set; }

    /// <summary>
    ///     The identity involved, where the signal is about one; <c>null</c> where it is about a
    ///     population.
    /// </summary>
    public Guid? ActorId { get; set; }
}
