using ArturRios.Heimdall.Shared.Security;
using ArturRios.Mediator.Command;

namespace ArturRios.Heimdall.Command.Input;

/// <summary>
///     Intent to logically delete a scope permission (UC-34). The permission is addressed by
///     <see cref="Id" /> within <see cref="ScopeId" />, both bound from the route. The deletion sets
///     the permission's <c>IsDeleted</c> flag and cascades to nothing. <see cref="ActingPersonId" />
///     and <see cref="ActingRole" /> are set by the controller from the authenticated caller and are
///     never bound from the request.
/// </summary>
public class DeleteScopePermissionCommand : BaseCommand, IActorScoped
{
    /// <summary>Public identifier of the scope the permission belongs to (bound from the route).</summary>
    public Guid ScopeId { get; set; }

    /// <summary>Public identifier of the permission to delete (bound from the route).</summary>
    public Guid Id { get; set; }

    public Guid ActingPersonId { get; set; }

    public int ActingRole { get; set; }
}