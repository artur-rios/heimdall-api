using ArturRios.Mediator.Query;

namespace ArturRios.Heimdall.Query.Output;

/// <summary>
///     Scope data returned by the view/list queries (UC-02). Only externally-facing <c>PublicId</c>
///     identifiers are exposed; internal Ids never leave the data layer.
/// </summary>
public class ScopeOutput : QueryOutput
{
    /// <summary>Public identifier of the scope.</summary>
    public Guid Id { get; set; }

    /// <summary>Scope display name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Scope description, if any.</summary>
    public string? Description { get; set; }

    /// <summary>Whether Google sign-in is enabled for the scope.</summary>
    public bool GoogleSignInEnabled { get; set; }

    /// <summary>
    ///     The lawful basis this tenant processes its users' data on (see <c>LegalBases</c>), or
    ///     <c>null</c> where it takes the deployment default (NFR-23).
    /// </summary>
    public int? DefaultLegalBasis { get; set; }

    /// <summary>Where this tenant publishes its own privacy notice, if it has one.</summary>
    public string? PrivacyNoticeUri { get; set; }

    /// <summary>Whether the scope is logically deleted.</summary>
    public bool IsDeleted { get; set; }

    /// <summary>Public identifiers of the scope's owners.</summary>
    public IEnumerable<Guid> OwnerIds { get; set; } = new List<Guid>();

    /// <summary>Creation timestamp.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>Last update timestamp.</summary>
    public DateTime UpdatedAt { get; set; }
}
