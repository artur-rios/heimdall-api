using ArturRios.Mediator.Command;

namespace ArturRios.Heimdall.Command.Output;

/// <summary>
///     Result of <see cref="Input.AcceptScopeOwnerInvitationCommand" /> (UC-60): the accepted invitation, which
///     is also the audit entry's target, and the scope the caller now owns.
/// </summary>
public class AcceptScopeOwnerInvitationCommandOutput : CommandOutput
{
    /// <summary>Public id of the accepted invitation.</summary>
    public Guid Id { get; set; }

    /// <summary>Public id of the scope the caller now co-owns.</summary>
    public Guid ScopeId { get; set; }
}
