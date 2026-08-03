using ArturRios.IdentityManager.Shared.Security;
using ArturRios.Mediator.Command;

namespace ArturRios.IdentityManager.Command.Input;

/// <summary>
///     Intent to logically delete a Google User (UC-28, FR-GO-15). The record is addressed by
///     <see cref="Id" /> within <see cref="ScopeId" />, both bound from the route. The deletion sets
///     the Google User's <c>IsDeleted</c> flag and cascades to nothing — a Google User owns no
///     dependent row. <see cref="ActingPersonId" />/<see cref="ActingRole" /> are set by the
///     controller from the authenticated caller and are never bound from the request.
/// </summary>
public class DeleteGoogleUserCommand : BaseCommand, IActorScoped
{
    /// <summary>Public identifier of the scope the Google User belongs to (bound from the route).</summary>
    public Guid ScopeId { get; set; }

    /// <summary>Public identifier of the Google User to delete (bound from the route).</summary>
    public Guid Id { get; set; }

    public Guid ActingPersonId { get; set; }

    public int ActingRole { get; set; }
}
