using ArturRios.Mediator.Query;

namespace ArturRios.Heimdall.Query.Output;

/// <summary>
///     Scope permission data returned by the UC-32 view/list queries. Only externally-facing
///     <c>PublicId</c> identifiers are exposed — the internal <c>bigint</c> scope foreign key never
///     leaves the data layer (SRD §4.0).
/// </summary>
public class ScopePermissionOutput : QueryOutput
{
    /// <summary>Public identifier of the scope permission.</summary>
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

    /// <summary>Whether the permission is logically deleted.</summary>
    public bool IsDeleted { get; set; }

    /// <summary>Creation timestamp.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>Last update timestamp.</summary>
    public DateTime UpdatedAt { get; set; }
}
