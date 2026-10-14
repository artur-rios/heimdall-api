using ArturRios.Heimdall.Shared.Security;
using ArturRios.Mediator.Command;

namespace ArturRios.Heimdall.Command.Input;

/// <summary>
///     Intent to send a verification email on a person's behalf (UC-51, FR-EV-06). The person is
///     <see cref="Id" />, from the route; <see cref="ActingPersonId" />/<see cref="ActingRole" /> are
///     set by the controller from the caller. There is no body: the link goes only to the person's
///     stored address, so nothing a caller could send would be used.
/// </summary>
public class SendPersonVerificationEmailCommand : BaseCommand, IActorScoped
{
    /// <summary>Public id of the person to email.</summary>
    public Guid Id { get; set; }

    public Guid ActingPersonId { get; set; }

    public int ActingRole { get; set; }
}
