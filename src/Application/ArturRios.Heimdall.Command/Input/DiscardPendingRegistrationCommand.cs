using ArturRios.Heimdall.Shared.Security;
using ArturRios.Mediator.Command;

namespace ArturRios.Heimdall.Command.Input;

/// <summary>
///     Intent to discard a self-registration that is awaiting confirmation (UC-50, FR-SR-12). Both
///     identifiers come from the route; <see cref="ActingPersonId" />/<see cref="ActingRole" /> are set
///     by the controller from the authenticated caller. There is no body.
/// </summary>
public class DiscardPendingRegistrationCommand : BaseCommand, IActorScoped
{
    /// <summary>Public id of the scope the registration belongs to.</summary>
    public Guid ScopeId { get; set; }

    /// <summary>Public id of the pending registration (UC-49's <c>id</c>).</summary>
    public Guid RegistrationId { get; set; }

    public Guid ActingPersonId { get; set; }

    public int ActingRole { get; set; }
}
