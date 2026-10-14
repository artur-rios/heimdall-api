namespace ArturRios.Heimdall.Shared.Messages;

/// <summary>
///     Canonical messages produced by the audit-trail listing (UC-48). Each is mapped to an HTTP status
///     code in <see cref="AuditMessageMap" />.
/// </summary>
/// <remarks>
///     Only the read has messages here. The trail is written by <c>AuditingCommandHandler</c>, which
///     records other use cases' messages and has none of its own.
/// </remarks>
public static class AuditMessages
{
    /// <summary>
    ///     UC-48 main flow and AF-48c: the entries matching the filters were listed — possibly none,
    ///     which is an empty page rather than a refusal.
    /// </summary>
    public const string AuditLogRetrieved = "Audit log entries retrieved.";

    /// <summary>AF-48a: the range's end (<c>To</c>) is earlier than its start (<c>From</c>).</summary>
    public const string InvalidDateRange = "The end of the date range must not be earlier than its start.";
}
