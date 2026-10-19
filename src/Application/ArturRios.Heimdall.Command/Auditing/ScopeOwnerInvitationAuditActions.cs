namespace ArturRios.Heimdall.Command.Auditing;

/// <summary>
///     The scope owner invitation events no command of their own names (NFR-09): an invitation opened by
///     UC-21, and one voided because it could no longer be honoured. Withdrawn, accepted and declined are
///     recorded under their commands' names. All five target the invitation's public id, the one identifier
///     that leads to both the scope and the invitee.
/// </summary>
public static class ScopeOwnerInvitationAuditActions
{
    public const string Created = "ScopeOwnerInvitationCreated";

    public const string Voided = "ScopeOwnerInvitationVoided";
}
