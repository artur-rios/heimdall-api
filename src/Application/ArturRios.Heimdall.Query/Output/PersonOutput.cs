using ArturRios.Mediator.Query;

namespace ArturRios.Heimdall.Query.Output;

/// <summary>
///     Person data returned by the UC-07 view/list queries. Only externally-facing <c>PublicId</c>
///     identifiers are exposed, and there is deliberately no field for <c>PasswordHash</c> or
///     <c>Salt</c>, so neither can escape through a projection.
/// </summary>
public class PersonOutput : QueryOutput
{
    /// <summary>Public identifier of the person.</summary>
    public Guid Id { get; set; }

    /// <summary>Full name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Email address.</summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>Assigned role value (see <c>Roles</c>).</summary>
    public int Role { get; set; }

    /// <summary>Whether the person's email has been verified.</summary>
    public bool EmailVerified { get; set; }

    /// <summary>Whether the person is logically deleted.</summary>
    public bool IsDeleted { get; set; }

    /// <summary>Public identifier of the scope the person belongs to as a User; <c>null</c> otherwise.</summary>
    public Guid? ScopeId { get; set; }

    /// <summary>Public identifiers of the scopes the person owns; empty for non-owners.</summary>
    public IEnumerable<Guid> OwnedScopeIds { get; set; } = new List<Guid>();

    /// <summary>Creation timestamp.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>Last update timestamp.</summary>
    public DateTime UpdatedAt { get; set; }
}
