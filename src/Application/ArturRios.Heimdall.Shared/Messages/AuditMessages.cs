namespace ArturRios.Heimdall.Shared.Messages;

/// <summary>
///     Canonical messages produced by the audit-trail listing (UC-48). Each is mapped to an HTTP status
///     code in <see cref="AuditMessageMap" />.
/// </summary>
/// <remarks>
///     Only the read has use-case messages here. The trail is written by <c>AuditingCommandHandler</c>,
///     which records other use cases' messages and has one of its own: the fixed reason for a command
///     that threw instead of answering.
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

    /// <summary>
    ///     The reason recorded when a command threw instead of answering (NFR-09) — any exception other
    ///     than the hash gate's, which records <see cref="AuthMessages.AuthenticationTemporarilyUnavailable" />.
    /// </summary>
    /// <remarks>
    ///     Fixed on purpose: an exception's own message can carry provider text or caller input, and the
    ///     reason column holds only the application's own canonical messages. The exception is logged
    ///     where it is handled; the entry only says that the attempt failed.
    /// </remarks>
    public const string CommandFailed = "The command failed before it could complete.";
}
