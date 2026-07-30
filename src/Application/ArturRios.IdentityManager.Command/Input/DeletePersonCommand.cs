using ArturRios.IdentityManager.Shared.Security;
using ArturRios.Mediator.Command;

namespace ArturRios.IdentityManager.Command.Input;

/// <summary>
///     Intent to logically delete a person (UC-09). The person is addressed by <see cref="Id" />,
///     bound from the route. The deletion sets the person's <c>IsDeleted</c> flag and cascades to
///     nothing — their join rows, tokens, and owned applications are left untouched.
///     <see cref="ActingPersonId" />/<see cref="ActingRole" /> are set by the controller from the
///     authenticated caller and are never bound from the request.
/// </summary>
public class DeletePersonCommand : BaseCommand, IActorScoped
{
    /// <summary>Public identifier of the person to delete (bound from the route).</summary>
    public Guid Id { get; set; }

    public Guid ActingPersonId { get; set; }

    public int ActingRole { get; set; }
}
