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
}
