using ArturRios.Heimdall.Command.Auditing;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Mediator.Command;

namespace ArturRios.Heimdall.Command.Output;

/// <summary>
///     Result of <see cref="Input.RequestErasureCommand" /> (UC-42): what the subject is owed and
///     by when.
/// </summary>
public class RequestErasureCommandOutput : CommandOutput, IAuditedSideEffects
{
    /// <summary>Public identifier of the identity the erasure was requested for.</summary>
    public Guid Id { get; set; }

    /// <summary>When the request was recorded.</summary>
    public DateTime RequestedAt { get; set; }

    /// <summary>
    ///     The deadline the erasure must not outlive. Returned to the subject because GDPR Art.
    ///     12(3) is an obligation owed to them, and one they cannot hold anyone to without knowing
    ///     it.
    /// </summary>
    public DateTime DueAt { get; set; }

    /// <summary>
    ///     Whether something prevents the erasure from proceeding yet. The request is recorded and
    ///     the deadline runs either way.
    /// </summary>
    public bool Blocked { get; set; }

    /// <summary>Why it is blocked, when it is; <c>null</c> otherwise.</summary>
    public string? BlockedReason { get; set; }

    /// <summary>
    ///     Whether the request lifted a restriction on processing the subject had in place (UC-44).
    ///     An erasure request is the subject's own instruction to stop holding the record, so it
    ///     ends the restriction that would otherwise keep the record from being erased (NFR-24) —
    ///     without the Art. 18(3) notice, since the subject is the one lifting it (UC-45).
    /// </summary>
    public bool RestrictionLifted { get; set; }

    /// <summary>
    ///     A lifted restriction is recorded in the audit trail as a lift of its own, so a reader
    ///     looking for when the restriction ended finds it under the same name whichever way it was
    ///     lifted.
    /// </summary>
    public IEnumerable<string> AuditedSideEffects() =>
        RestrictionLifted ? [nameof(LiftProcessingRestrictionCommand)] : [];
}
