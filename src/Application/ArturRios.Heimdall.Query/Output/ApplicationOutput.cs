using ArturRios.Mediator.Query;

namespace ArturRios.Heimdall.Query.Output;

/// <summary>
///     Application data returned by the UC-17 view/list queries. Only externally-facing
///     <c>PublicId</c> identifiers are exposed — the internal <c>bigint</c> scope and owner foreign
///     keys never leave the data layer (SRD §4.0).
/// </summary>
public class ApplicationOutput : QueryOutput
{
    /// <summary>Public identifier of the application.</summary>
    public Guid Id { get; init; }

    /// <summary>Application display name.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Public identifier of the scope the application belongs to.</summary>
    public Guid ScopeId { get; init; }

    /// <summary>Public identifier of the <c>ScopeAdmin</c> who owns the application.</summary>
    public Guid OwnerId { get; init; }

    /// <summary>Whether the application is logically deleted.</summary>
    public bool IsDeleted { get; init; }

    /// <summary>Creation timestamp.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>Last update timestamp.</summary>
    public DateTime UpdatedAt { get; set; }
}
