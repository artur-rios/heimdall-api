using System.Text.Json.Serialization;
using ArturRios.Mediator.Command;

namespace ArturRios.Heimdall.Command.Input;

/// <summary>
///     Intent to update an existing scope's name and description (UC-03). The scope is addressed by
///     its <c>PublicId</c> (GUID), bound from the route. PUT semantics: both <see cref="Name" /> and
///     <see cref="Description" /> are replaced; a null <see cref="Description" /> clears it.
///     <see cref="Id" /> is <c>[JsonIgnore]</c>, so it is not deserialized from the body and does not
///     appear in the request schema.
/// </summary>
public class UpdateScopeCommand : BaseCommand
{
    /// <summary>Public identifier of the scope to update (bound from the route).</summary>
    [JsonIgnore]
    public Guid Id { get; set; }

    /// <summary>New scope display name. Required and must be unique across all scopes.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>New description of the scope's purpose. Null clears any existing description.</summary>
    public string? Description { get; set; }

    /// <summary>
    ///     The lawful basis this tenant processes its users' data on (see <c>LegalBases</c>), or
    ///     <c>null</c> to take the deployment default of contract performance (NFR-23).
    /// </summary>
    /// <remarks>
    ///     <c>Consent</c> is refused. It is withdrawable at any moment and withdrawal must be as
    ///     easy as giving it, which needs a path that does not exist here — accepting the value
    ///     would let a tenant record a basis the system could not honour.
    /// </remarks>
    public int? DefaultLegalBasis { get; set; }

    /// <summary>
    ///     Where this tenant publishes its own privacy notice, or <c>null</c> if it has none
    ///     distinct from Heimdall's.
    /// </summary>
    public string? PrivacyNoticeUri { get; set; }
}
