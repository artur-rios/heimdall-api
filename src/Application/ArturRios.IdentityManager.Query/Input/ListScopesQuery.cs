using ArturRios.Mediator.Query;

namespace ArturRios.IdentityManager.Query.Input;

/// <summary>
///     Request to list scopes with pagination and optional filtering (UC-02, FR-SC-03). Page number
///     and size are inherited from <see cref="BaseQuery" />.
/// </summary>
public class ListScopesQuery : BaseQuery
{
    /// <summary>Optional case-insensitive substring filter on the scope name.</summary>
    public string? Name { get; set; }

    /// <summary>When <c>true</c>, logically deleted scopes are included in the results (FR-SC-07).</summary>
    public bool IncludeDeleted { get; set; }
}
