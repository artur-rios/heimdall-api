using System.Text.Json.Serialization;
using ArturRios.Heimdall.Shared.Security;
using ArturRios.Mediator.Command;

namespace ArturRios.Heimdall.Command.Input;

/// <summary>
///     Intent to change an application's name and owner (UC-18, FR-AP-06). The application is
///     addressed by <see cref="Id" /> within <see cref="ScopeId" />, both assigned from the route.
///     PUT semantics: <see cref="Name" /> and <see cref="OwnerId" /> are replaced, so a caller
///     changing only the name resubmits the current owner. The scope is a route qualifier, never a
///     field to write — FR-AP-02 fixes an application's scope at creation time.
///     <see cref="ActingPersonId" />/<see cref="ActingRole" /> are set by the controller from the
///     authenticated caller. All four are <c>[JsonIgnore]</c>, so they are not deserialized from the
///     body and do not appear in the request schema.
/// </summary>
public class UpdateApplicationCommand : BaseCommand, IActorScoped
{
    /// <summary>Public identifier of the scope the application belongs to (assigned from the route).</summary>
    [JsonIgnore]
    public Guid ScopeId { get; set; }

    /// <summary>Public identifier of the application to update (assigned from the route).</summary>
    [JsonIgnore]
    public Guid Id { get; set; }

    /// <summary>New application display name. Required, max 200 characters.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    ///     Public identifier of the person that will own the application (FR-AP-03). Verified only
    ///     when it differs from the current owner (UC-18 main flow step 4).
    /// </summary>
    public Guid OwnerId { get; set; }

    [JsonIgnore]
    public Guid ActingPersonId { get; set; }

    [JsonIgnore]
    public int ActingRole { get; set; }
}
