using ArturRios.IdentityManager.Shared.Security;
using ArturRios.Mediator.Command;

namespace ArturRios.IdentityManager.Command.Input;

/// <summary>
///     Intent to promote an existing <c>User</c> of a scope to <c>ScopeAdmin</c>, making them a
///     co-owner of that scope (UC-23, FR-SC-08/FR-SC-13/FR-RO-03). Both <see cref="ScopeId" /> and
///     <see cref="PersonId" /> are bound from the route — the request carries no body — while
///     <see cref="ActingPersonId" />/<see cref="ActingRole" /> are set by the controller from the
///     authenticated caller (for the AF-23c ownership check) and are never bound from the request.
/// </summary>
public class PromoteScopeUserCommand : BaseCommand, IActorScoped
{
    /// <summary>Public identifier of the scope the person belongs to and will own (bound from the route).</summary>
    public Guid ScopeId { get; set; }

    /// <summary>Public identifier of the person to promote (bound from the route).</summary>
    public Guid PersonId { get; set; }

    public Guid ActingPersonId { get; set; }

    public int ActingRole { get; set; }
}
