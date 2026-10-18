using ArturRios.Heimdall.Shared.Security;
using ArturRios.Mediator.Command;

namespace ArturRios.Heimdall.Command.Input;

/// <summary>
///     Intent to decline a scope owner invitation (UC-61, FR-SC-17). The id comes from the route and the actor —
///     who must be the invitee — from the bearer token. There is no body.
/// </summary>
public class DeclineScopeOwnerInvitationCommand : BaseCommand, IActorScoped
{
    /// <summary>Public id of the invitation (UC-59's <c>id</c>).</summary>
    public Guid InvitationId { get; set; }

    public Guid ActingPersonId { get; set; }

    public int ActingRole { get; set; }
}
