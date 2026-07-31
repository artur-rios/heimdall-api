using ArturRios.IdentityManager.Shared.Security;
using ArturRios.Mediator.Command;

namespace ArturRios.IdentityManager.Command.Input;

/// <summary>
///     Intent to change an application's name and owner (UC-18, FR-AP-06). The application is
///     addressed by <see cref="Id" /> within <see cref="ScopeId" />, both bound from the route. PUT
///     semantics: <see cref="Name" /> and <see cref="OwnerId" /> are replaced, so a caller changing
///     only the name resubmits the current owner. The scope is a route qualifier, never a field to
///     write — FR-AP-02 fixes an application's scope at creation time.
///     <see cref="ActingPersonId" />/<see cref="ActingRole" /> are set by the controller from the
///     authenticated caller and are never bound from the body.
/// </summary>
public class UpdateApplicationCommand : BaseCommand, IActorScoped
{
    /// <summary>Public identifier of the scope the application belongs to (bound from the route).</summary>
    public Guid ScopeId { get; set; }

    /// <summary>Public identifier of the application to update (bound from the route).</summary>
    public Guid Id { get; set; }

    /// <summary>New application display name. Required, max 200 characters.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    ///     Public identifier of the person that will own the application (FR-AP-03). Verified only
    ///     when it differs from the current owner (UC-18 main flow step 4).
    /// </summary>
    public Guid OwnerId { get; set; }

    public Guid ActingPersonId { get; set; }

    public int ActingRole { get; set; }
}
