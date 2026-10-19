using ArturRios.Mediator.Command;

namespace ArturRios.Heimdall.Command.Output;

/// <summary>
///     Result of <see cref="Input.WithdrawScopeOwnerInvitationCommand" /> (UC-58). The endpoint answers 204
///     without a body; <see cref="Id" /> is what the audit entry records as its target.
/// </summary>
public class WithdrawScopeOwnerInvitationCommandOutput : CommandOutput
{
    /// <summary>Public id of the withdrawn invitation.</summary>
    public Guid Id { get; set; }
}
