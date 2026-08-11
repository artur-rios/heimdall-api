using ArturRios.Heimdall.Shared.Security;
using ArturRios.Mediator.Command;

namespace ArturRios.Heimdall.Command.Input;

/// <summary>
///     Intent to logically delete an application (UC-19, FR-AP-07). The application is addressed by
///     <see cref="Id" /> within <see cref="ScopeId" />, both bound from the route. The deletion sets
///     the application's <c>IsDeleted</c> flag and cascades to nothing — an application owns no
///     dependent row. <see cref="ActingPersonId" />/<see cref="ActingRole" /> are set by the
///     controller from the authenticated caller and are never bound from the request.
/// </summary>
public class DeleteApplicationCommand : BaseCommand, IActorScoped
{
    /// <summary>Public identifier of the scope the application belongs to (bound from the route).</summary>
    public Guid ScopeId { get; set; }

    /// <summary>Public identifier of the application to delete (bound from the route).</summary>
    public Guid Id { get; set; }

    public Guid ActingPersonId { get; set; }

    public int ActingRole { get; set; }
}
