using System.Text.Json.Serialization;
using ArturRios.Heimdall.Command.Auditing;
using ArturRios.Mediator.Command;

namespace ArturRios.Heimdall.Command.Output;

/// <summary>Result of <see cref="Input.ReapplyErasuresCommand" /> (NFR-25).</summary>
public class ReapplyErasuresCommandOutput : CommandOutput, IAuditedSideEffects
{
    /// <summary>Identities anonymised by this run.</summary>
    public int Anonymised { get; set; }

    /// <summary>
    ///     Identities that were already anonymised, so the restore did not in fact bring them back.
    /// </summary>
    public int AlreadyAnonymised { get; set; }

    /// <summary>
    ///     Identifiers that matched nothing. Reported rather than treated as an error: a ledger
    ///     naming somebody the restore did not reinstate is the expected case, not a mistake.
    /// </summary>
    public IEnumerable<Guid> NotFound { get; set; } = [];

    /// <summary>
    ///     Unended scope owner invitations of the persons this run anonymised again, voided (FR-SC-17).
    ///     Each is recorded in the audit trail as <c>ScopeOwnerInvitationVoided</c>, on that invitation.
    /// </summary>
    public int ScopeOwnerInvitationsVoided => VoidedScopeOwnerInvitationIds.Count;

    /// <summary>
    ///     Public identifiers of those invitations, which their audit entries target (NFR-09). In no
    ///     particular order, and kept out of the serialized response: the count says what a caller needs.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyCollection<Guid> VoidedScopeOwnerInvitationIds { get; init; } = [];

    public IEnumerable<AuditedSideEffect> AuditedSideEffects() =>
        VoidedScopeOwnerInvitationIds.Select(id => new AuditedSideEffect(ScopeOwnerInvitationAuditActions.Voided, id));
}
