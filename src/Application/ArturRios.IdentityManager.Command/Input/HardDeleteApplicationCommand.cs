using ArturRios.Mediator.Command;

namespace ArturRios.IdentityManager.Command.Input;

/// <summary>
///     Intent to permanently (hard) delete an application (UC-20, FR-AP-08). The application is
///     addressed by <see cref="Id" /> within <see cref="ScopeId" />, both bound from the route.
///     Removing it cascades to nothing — no entity carries a foreign key to an application, and the
///     scope and owner it points at are left intact. The command carries no acting person: UC-20's
///     only actor is the System Admin and the endpoint's role requirement settles that entirely, so
///     the handler has no data-dependent rule left to apply.
/// </summary>
public class HardDeleteApplicationCommand : BaseCommand
{
    /// <summary>Public identifier of the scope the application belongs to (bound from the route).</summary>
    public Guid ScopeId { get; set; }

    /// <summary>Public identifier of the application to hard-delete (bound from the route).</summary>
    public Guid Id { get; set; }
}
