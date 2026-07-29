using ArturRios.Mediator.Command;

namespace ArturRios.IdentityManager.Command.Input;

/// <summary>
///     Intent to create a brand-new <c>ScopeAdmin</c> person directly as a co-owner of a scope
///     (UC-06 path c, FR-SC-12). <see cref="ScopeId" /> comes from the route;
///     <see cref="ActingPersonId" />/<see cref="ActingRole" /> are set by the controller from the
///     authenticated caller (for the AF-06e ownership check) and are never bound from the body.
/// </summary>
public class CreateScopeOwnerCommand : BaseCommand, IActorScopedCommand
{
    public Guid ScopeId { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Email { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    public long ActingPersonId { get; set; }

    public int ActingRole { get; set; }
}
