using ArturRios.Mediator.Command;

namespace ArturRios.Heimdall.Command.Output;

/// <summary>Result of <see cref="Input.DeclineScopeOwnerInvitationCommand" /> (UC-61).</summary>
public class DeclineScopeOwnerInvitationCommandOutput : CommandOutput
{
    /// <summary>Public id of the declined invitation, also the audit entry's target.</summary>
    public Guid Id { get; set; }
}
