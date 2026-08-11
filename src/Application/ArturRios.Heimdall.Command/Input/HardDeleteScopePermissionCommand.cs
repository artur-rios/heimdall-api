using ArturRios.Mediator.Command;

namespace ArturRios.Heimdall.Command.Input;

/// <summary>
///     Intent to permanently (hard) delete a scope permission (UC-35). The permission is addressed by
///     <see cref="Id" /> within <see cref="ScopeId" />, both bound from the route. Removing it
///     cascades to nothing — no entity carries a foreign key to a scope permission, and the scope it
///     points at is left intact. The command carries no acting person: UC-35's only actor is the
///     System Admin and the endpoint's role requirement settles that entirely, so the handler has no
///     data-dependent rule left to apply.
/// </summary>
public class HardDeleteScopePermissionCommand : BaseCommand
{
    /// <summary>Public identifier of the scope the permission belongs to (bound from the route).</summary>
    public Guid ScopeId { get; set; }

    /// <summary>Public identifier of the permission to hard-delete (bound from the route).</summary>
    public Guid Id { get; set; }
}