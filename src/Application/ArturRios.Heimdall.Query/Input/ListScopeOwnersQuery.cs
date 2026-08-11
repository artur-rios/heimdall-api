using ArturRios.Heimdall.Shared.Security;
using ArturRios.Mediator.Query;

namespace ArturRios.Heimdall.Query.Input;

/// <summary>
///     Request to list the <c>ScopeAdmin</c> owners of a scope, with pagination and optional
///     filtering (UC-07, FR-PE-04). <see cref="ScopeId" /> comes from the route;
///     <see cref="ActingPersonId" />/<see cref="ActingRole" /> are set by the controller from the
///     authenticated caller and are never taken from the request.
/// </summary>
public class ListScopeOwnersQuery : BaseQuery, IActorScoped
{
    /// <summary>Public identifier of the scope whose owners are listed.</summary>
    public Guid ScopeId { get; set; }

    /// <summary>Optional case-insensitive substring filter on the owner's name.</summary>
    public string? Name { get; set; }

    /// <summary>Optional case-insensitive substring filter on the owner's email.</summary>
    public string? Email { get; set; }

    /// <summary>When <c>true</c>, logically deleted owners are included in the results (FR-PE-08).</summary>
    public bool IncludeDeleted { get; set; }

    public Guid ActingPersonId { get; set; }

    public int ActingRole { get; set; }
}
