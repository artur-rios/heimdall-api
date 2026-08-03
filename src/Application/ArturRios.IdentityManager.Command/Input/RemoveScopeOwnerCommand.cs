using ArturRios.IdentityManager.Shared.Security;
using ArturRios.Mediator.Command;

namespace ArturRios.IdentityManager.Command.Input;

/// <summary>
///     Intent to remove a person's ownership of a scope (UC-22, FR-SC-08/FR-SC-10). Both
///     <see cref="ScopeId" /> and <see cref="PersonId" /> are bound from the route — the request
///     carries no body — while <see cref="ActingPersonId" />/<see cref="ActingRole" /> are set by the
///     controller from the authenticated caller (for the AF-22c ownership check) and are never bound
///     from the request.
/// </summary>
public class RemoveScopeOwnerCommand : BaseCommand, IActorScoped
{
    /// <summary>Public identifier of the scope losing an owner (bound from the route).</summary>
    public Guid ScopeId { get; set; }

    /// <summary>Public identifier of the person whose ownership is removed (bound from the route).</summary>
    public Guid PersonId { get; set; }

    public Guid ActingPersonId { get; set; }

    public int ActingRole { get; set; }
}
