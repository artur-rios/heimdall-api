using ArturRios.Heimdall.Shared.Security;
using ArturRios.Mediator.Command;

namespace ArturRios.Heimdall.Command.Input;

/// <summary>
///     Intent to withdraw an open scope owner invitation (UC-58, FR-SC-15). Both identifiers come from the
///     route; <see cref="ActingPersonId" />/<see cref="ActingRole" /> are set by the controller from the
///     authenticated caller. There is no body.
/// </summary>
public class WithdrawScopeOwnerInvitationCommand : BaseCommand, IActorScoped
{
    /// <summary>Public id of the scope the invitation is for.</summary>
    public Guid ScopeId { get; set; }

    /// <summary>Public id of the invitation (UC-21's <c>invitationId</c>, UC-58's <c>id</c>).</summary>
    public Guid InvitationId { get; set; }

    public Guid ActingPersonId { get; set; }

    public int ActingRole { get; set; }
}
