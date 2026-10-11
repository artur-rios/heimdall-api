namespace ArturRios.Heimdall.Command.Auditing;

/// <summary>
///     An output whose command, besides its own write, carried out an action that has a use case of
///     its own — and that a reader of the audit trail would look for under that action's name.
///     <see cref="AuditingCommandHandler{TCommand,TOutput}" /> records one further entry for each,
///     on the same target, after the command's own entry (NFR-09).
/// </summary>
/// <remarks>
///     <para>
///         The trail otherwise holds one entry per command, naming the command. That is enough when
///         everything a command does is implied by its name, and misleading when it is not: an
///         erasure request that also lifts the subject's restriction (UC-42) would leave the
///         restriction's end recorded nowhere a reader looking for restriction lifts (UC-45) would
///         find it.
///     </para>
///     <para>
///         A method rather than a property, so it never reaches the serialized response or the
///         OpenAPI document.
///     </para>
/// </remarks>
public interface IAuditedSideEffects
{
    /// <summary>
    ///     The action name to record for each side effect that took place — the name of the command
    ///     that performs the same action on its own, e.g. <c>"LiftProcessingRestrictionCommand"</c>.
    ///     Empty when there were none.
    /// </summary>
    IEnumerable<string> AuditedSideEffects();
}
