using ArturRios.Mediator.Command;

namespace ArturRios.Heimdall.Command.Output;

/// <summary>
///     The scope permission created by <see cref="Input.CreateScopePermissionCommand" /> (UC-31).
///     Only externally-facing <c>PublicId</c> identifiers are exposed; the internal Ids that link the
///     row to its scope never leave the data layer.
/// </summary>
public class CreateScopePermissionCommandOutput : CommandOutput
{
    /// <summary>Public identifier of the created permission.</summary>
    public Guid Id { get; set; }

    /// <summary>Permission display name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Permission description.</summary>
    public string? Description { get; set; }

    /// <summary>
    ///     Whether the permission's <see cref="Name" /> is added as a claim on the JWT issued to
    ///     identities acting within the owning scope.
    /// </summary>
    public bool IncludeAsJwtClaim { get; set; }

    /// <summary>Public identifier of the scope the permission belongs to.</summary>
    public Guid ScopeId { get; set; }

    /// <summary>Creation timestamp.</summary>
    public DateTime CreatedAt { get; set; }
}