using ArturRios.Mediator.Query;

namespace ArturRios.Heimdall.Query.Input;

/// <summary>
///     Asks what in the recent record is worth telling somebody about (NFR-26).
/// </summary>
/// <remarks>
///     A query rather than a command, unlike the retention passes: it changes nothing, and it runs
///     every few minutes — auditing each run would fill the trail with entries saying "looked, saw
///     nothing" and bury the writes NFR-09 exists to record. What it finds is logged, and what it
///     finds is about writes that were already audited when they happened.
/// </remarks>
public class DetectSecuritySignalsQuery : BaseQuery;
