using ArturRios.Mediator.Command;

namespace ArturRios.Heimdall.Command.Output;

/// <summary>
///     The scope permission as it stands after a UC-33 update. Only externally-facing <c>PublicId</c>
///     identifiers are exposed; the internal Ids that link the row to its scope never leave the data
///     layer.
/// </summary>
public class UpdateScopePermissionCommandOutput : CommandOutput
{
    /// <summary>Public identifier of the updated permission.</summary>
    public Guid Id { get; init; }

    /// <summary>Permission display name after the update.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Permission description after the update.</summary>
    public string? Description { get; init; }

    /// <summary>
    ///     Whether the permission's <see cref="Name" /> is added as a claim on the JWT issued to
    ///     identities acting within the owning scope.
    /// </summary>
    public bool IncludeAsJwtClaim { get; init; }

    /// <summary>Public identifier of the scope the permission belongs to (unchanged by this operation).</summary>
    public Guid ScopeId { get; init; }

    /// <summary>Creation timestamp.</summary>
    public DateTime CreatedAt { get; init; }

    /// <summary>Update timestamp, stamped by this operation.</summary>
    public DateTime UpdatedAt { get; init; }
}