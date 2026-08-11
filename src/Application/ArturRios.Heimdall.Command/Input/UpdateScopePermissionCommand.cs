using ArturRios.Heimdall.Shared.Security;
using ArturRios.Mediator.Command;

namespace ArturRios.Heimdall.Command.Input;

/// <summary>
///     Intent to change a scope permission's name, description, and JWT-claim flag (UC-33). The
///     permission is addressed by <see cref="Id" /> within <see cref="ScopeId" />, both bound from
///     the route. PUT semantics: <see cref="Name" />, <see cref="Description" />, and
///     <see cref="IncludeAsJwtClaim" /> are replaced, so a caller changing only one resubmits the
///     current values of the others. The scope is a route qualifier, never a field to write — a
///     permission's scope is fixed at creation time. <see cref="ActingPersonId" />/<see cref="ActingRole" />
///     are set by the controller from the authenticated caller and are never bound from the body.
/// </summary>
public class UpdateScopePermissionCommand : BaseCommand, IActorScoped
{
    /// <summary>Public identifier of the scope the permission belongs to (bound from the route).</summary>
    public Guid ScopeId { get; set; }

    /// <summary>Public identifier of the permission to update (bound from the route).</summary>
    public Guid Id { get; set; }

    /// <summary>New permission display name. Required, max 200 characters.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>New permission description. Max 500 characters.</summary>
    public string? Description { get; set; }

    /// <summary>
    ///     New value of the flag controlling whether the permission's <see cref="Name" /> is added as
    ///     a claim on the JWT issued to identities acting within the owning scope.
    /// </summary>
    public bool IncludeAsJwtClaim { get; set; }

    public Guid ActingPersonId { get; set; }

    public int ActingRole { get; set; }
}