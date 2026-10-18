namespace ArturRios.Heimdall.Command.Auditing;

/// <summary>
///     An output whose command, besides its own write, carried out an action that has a use case of
///     its own — and that a reader of the audit trail would look for under that action's name.
///     <see cref="AuditingCommandHandler{TCommand,TOutput}" /> records one further entry for each,
///     after the command's own entry (NFR-09): on the entity the side effect names, or on the
///     command's own target when it names none.
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
    ///     One entry for each side effect that took place, naming the action to record — the name of
    ///     the command that performs the same action on its own, e.g.
    ///     <c>"LiftProcessingRestrictionCommand"</c>, or an event no command names, e.g.
    ///     <c>"ScopeOwnerInvitationVoided"</c>. Empty when there were none.
    /// </summary>
    IEnumerable<AuditedSideEffect> AuditedSideEffects();
}

/// <summary>One side effect of a command, as <see cref="IAuditedSideEffects" /> reports it.</summary>
/// <param name="Action">The action name the audit entry records.</param>
/// <param name="TargetId">
///     The public identifier of the entity the side effect acted on, when that is not the command's own
///     target — an anonymisation pass has none of its own, yet each invitation it voids is looked up by
///     its id. <c>null</c> records the entry on the command's target.
/// </param>
public sealed record AuditedSideEffect(string Action, Guid? TargetId = null);
