using ArturRios.Mediator.Command;

namespace ArturRios.IdentityManager.Command.Input;

/// <summary>
///     Intent to create a new scope (UC-01), designating one or more existing
///     <c>ScopeAdmin</c> persons as its initial owners. Owners are referenced by their
///     <c>PublicId</c> (GUID), never by internal Id.
/// </summary>
public class CreateScopeCommand : BaseCommand
{
    /// <summary>Scope display name. Required and must be unique across all scopes.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Optional description of the scope's purpose.</summary>
    public string? Description { get; set; }

    /// <summary>
    ///     Public identifiers of the persons to designate as initial owners. Each must reference an
    ///     existing, non-logically-deleted person with the <c>ScopeAdmin</c> role. At least one is required.
    /// </summary>
    public IEnumerable<Guid> OwnerIds { get; set; } = new List<Guid>();
}
