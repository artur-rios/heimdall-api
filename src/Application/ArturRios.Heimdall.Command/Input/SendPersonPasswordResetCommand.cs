using ArturRios.Heimdall.Shared.Security;
using ArturRios.Mediator.Command;

namespace ArturRios.Heimdall.Command.Input;

/// <summary>
///     Intent to send a password-reset email on a person's behalf (UC-52, FR-PR-05). The person is
///     <see cref="Id" />, from the route; <see cref="ActingPersonId" />/<see cref="ActingRole" /> are
///     set by the controller from the caller. There is no body, and no password is ever accepted: the
///     person sets one themselves through the link (UC-13).
/// </summary>
public class SendPersonPasswordResetCommand : BaseCommand, IActorScoped
{
    /// <summary>Public id of the person to email.</summary>
    public Guid Id { get; set; }

    public Guid ActingPersonId { get; set; }

    public int ActingRole { get; set; }
}
