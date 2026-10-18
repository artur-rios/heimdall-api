using System.Text.Json.Serialization;
using ArturRios.Heimdall.Command.Auditing;
using ArturRios.Mediator.Command;

namespace ArturRios.Heimdall.Command.Output;

/// <summary>
///     Result of <see cref="Input.AnonymiseExpiredDeletionsCommand" />: how many identities the run
///     anonymised. Zero is an ordinary successful run.
/// </summary>
public class AnonymiseExpiredDeletionsCommandOutput : CommandOutput, IAuditedSideEffects
{
    /// <summary>Persons anonymised.</summary>
    public int PersonsAnonymised { get; set; }

    /// <summary>Google Users anonymised.</summary>
    public int GoogleUsersAnonymised { get; set; }

    /// <summary>Dependent rows removed alongside them — tokens and two-factor configurations.</summary>
    public int DependentsRemoved { get; set; }

    /// <summary>
    ///     Public identifiers of the identities this run anonymised — the erasure ledger (NFR-25).
    /// </summary>
    /// <remarks>
    ///     Logged by the scheduled service so the list survives outside the database, which is what
    ///     makes a restore reconcilable: a backup taken before an erasure request reinstates the
    ///     person with no trace of the request, and nothing inside the database then knows to act.
    ///     These identifiers are safe to keep and to ship off the host precisely because the
    ///     identities they name have been anonymised — a <c>PublicId</c> that resolves to nobody is
    ///     not personal data.
    /// </remarks>
    public IEnumerable<Guid> AnonymisedIds { get; set; } = [];

    /// <summary>
    ///     Unended scope owner invitations of the persons this run anonymised, voided (FR-SC-17).
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

    /// <summary>Identities anonymised across both tables.</summary>
    public int TotalAnonymised => PersonsAnonymised + GoogleUsersAnonymised;
}
