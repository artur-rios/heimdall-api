namespace ArturRios.Heimdall.Command.Auditing;

/// <summary>Persists one audit trail entry (NFR-09). See <see cref="AuditingCommandHandler{TCommand,TOutput}" />.</summary>
public interface IAuditLogWriter
{
    /// <param name="action">The command's CLR type name, e.g. <c>"CreateApplicationCommand"</c>.</param>
    /// <param name="targetId">Best-effort public identifier of the affected entity, if resolvable.</param>
    /// <param name="succeeded">Whether the operation succeeded.</param>
    /// <param name="failureReason">
    ///     The first error reported, when it failed; <c>null</c> on success. Always one of the
    ///     application's canonical messages or the persistence layer's classified ones — never a
    ///     caller-supplied value.
    /// </param>
    Task WriteAsync(string action, Guid? targetId, bool succeeded, string? failureReason);
}
