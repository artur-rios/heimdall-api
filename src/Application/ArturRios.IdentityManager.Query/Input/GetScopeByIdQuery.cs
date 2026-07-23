using ArturRios.Mediator.Query;

namespace ArturRios.IdentityManager.Query.Input;

/// <summary>
///     Request to retrieve a single scope by its <c>PublicId</c> (UC-02, FR-SC-02). The pagination
///     members inherited from <see cref="BaseQuery" /> are unused for a by-id lookup.
/// </summary>
public class GetScopeByIdQuery : BaseQuery
{
    /// <summary>Public identifier of the scope to retrieve.</summary>
    public Guid Id { get; set; }

    /// <summary>When <c>true</c>, a logically deleted scope is still returned (FR-SC-07).</summary>
    public bool IncludeDeleted { get; set; }
}
