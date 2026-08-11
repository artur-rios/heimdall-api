namespace ArturRios.Heimdall.Command.Auditing;

/// <summary>Persists one audit trail entry (NFR-09). See <see cref="AuditingCommandHandler{TCommand,TOutput}" />.</summary>
public interface IAuditLogWriter
{
    /// <param name="action">The command's CLR type name, e.g. <c>"CreateApplicationCommand"</c>.</param>
    /// <param name="targetId">Best-effort public identifier of the affected entity, if resolvable.</param>
    Task WriteAsync(string action, Guid? targetId);
}
