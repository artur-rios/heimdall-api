using ArturRios.Heimdall.Shared.Security;
using ArturRios.Mediator.Command;

namespace ArturRios.Heimdall.Command.Input;

/// <summary>
///     Intent to register an application within a scope (UC-16). <see cref="ScopeId" /> comes from the
///     route; <see cref="ActingPersonId" />/<see cref="ActingRole" /> are set by the controller from
///     the authenticated caller (for the AF-16c self-owner rule and the Scope Admin ownership check)
///     and are never bound from the body.
/// </summary>
public class CreateApplicationCommand : BaseCommand, IActorScoped
{
    public Guid ScopeId { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>Public identifier of the person that will own the application (FR-AP-03).</summary>
    public Guid OwnerId { get; set; }

    public Guid ActingPersonId { get; set; }

    public int ActingRole { get; set; }
}
