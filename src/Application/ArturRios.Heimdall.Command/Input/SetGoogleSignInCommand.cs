using System.Text.Json.Serialization;
using ArturRios.Heimdall.Shared.Security;
using ArturRios.Mediator.Command;

namespace ArturRios.Heimdall.Command.Input;

/// <summary>
///     Intent to turn Google Sign-In on or off for a scope (UC-24, FR-GO-01/FR-GO-02). The scope is
///     addressed by its <c>PublicId</c> (GUID), assigned from the route.
///     <see cref="ActingPersonId" />/<see cref="ActingRole" /> are set by the controller from the
///     authenticated caller (for the AF-24b ownership check) and are never bound from the request.
///     All three are <c>[JsonIgnore]</c>, so they are not deserialized from the body and do not
///     appear in the request schema.
/// </summary>
public class SetGoogleSignInCommand : BaseCommand, IActorScoped
{
    /// <summary>Public identifier of the scope whose setting is changing (assigned from the route).</summary>
    [JsonIgnore]
    public Guid Id { get; set; }

    /// <summary>
    ///     The value to write to the scope's <c>GoogleSignInEnabled</c> flag. Nullable on purpose: a
    ///     plain <c>bool</c> would bind a body that omits the field to <c>false</c>, so a malformed
    ///     request would silently *disable* Google Sign-In. The validator refuses <c>null</c>, which
    ///     turns that into an explicit 400 (NFR-10).
    /// </summary>
    public bool? Enabled { get; set; }

    [JsonIgnore]
    public Guid ActingPersonId { get; set; }

    [JsonIgnore]
    public int ActingRole { get; set; }
}
