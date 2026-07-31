using ArturRios.IdentityManager.Shared.Security;
using ArturRios.Mediator.Command;

namespace ArturRios.IdentityManager.Command.Input;

/// <summary>
///     Intent to add an existing <c>ScopeAdmin</c> person as an additional owner of a scope (UC-21,
///     FR-SC-08/FR-SC-09). Both <see cref="ScopeId" /> and <see cref="PersonId" /> are bound from the
///     route — the request carries no body — while <see cref="ActingPersonId" />/
///     <see cref="ActingRole" /> are set by the controller from the authenticated caller (for the
///     AF-21c ownership check) and are never bound from the request.
/// </summary>
public class AddScopeOwnerCommand : BaseCommand, IActorScoped
{
    /// <summary>Public identifier of the scope gaining an owner (bound from the route).</summary>
    public Guid ScopeId { get; set; }

    /// <summary>Public identifier of the person to make an owner (bound from the route).</summary>
    public Guid PersonId { get; set; }

    public Guid ActingPersonId { get; set; }

    public int ActingRole { get; set; }
}
