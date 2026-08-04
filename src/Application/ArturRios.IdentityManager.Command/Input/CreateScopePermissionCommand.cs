using ArturRios.IdentityManager.Shared.Security;
using ArturRios.Mediator.Command;

namespace ArturRios.IdentityManager.Command.Input;

/// <summary>
///     Intent to register a scope-specific permission (UC-31). <see cref="ScopeId" /> comes from the
///     route; <see cref="ActingPersonId" />/<see cref="ActingRole" /> are set by the controller from
///     the authenticated caller (for the AF-31e scope-ownership check) and are never bound from the
///     body. The <see cref="IncludeAsJwtClaim" /> flag controls whether the permission's
///     <see cref="Name" /> is folded into the JWT issued to identities acting within the owning
///     scope.
/// </summary>
public class CreateScopePermissionCommand : BaseCommand, IActorScoped
{
    public Guid ScopeId { get; set; }

    /// <summary>Permission display name. Required, max 200 characters.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Optional description of the permission's purpose. Max 500 characters.</summary>
    public string? Description { get; set; }

    /// <summary>
    ///     When <c>true</c>, this permission's <see cref="Name" /> is added as a claim on the JWT
    ///     issued to identities acting within the owning scope. Defaults to <c>false</c>.
    /// </summary>
    public bool IncludeAsJwtClaim { get; set; }

    public Guid ActingPersonId { get; set; }

    public int ActingRole { get; set; }
}