using ArturRios.IdentityManager.Shared.Security;
using ArturRios.Mediator.Query;

namespace ArturRios.IdentityManager.Query.Input;

/// <summary>
///     Request to list the <c>User</c> persons of a scope, with pagination and optional filtering
///     (UC-07, FR-PE-04). <see cref="ScopeId" /> comes from the route;
///     <see cref="ActingPersonId" />/<see cref="ActingRole" /> are set by the controller from the
///     authenticated caller and are never taken from the request.
/// </summary>
public class ListScopePersonsQuery : BaseQuery, IActorScoped
{
    /// <summary>Public identifier of the scope whose Users are listed.</summary>
    public Guid ScopeId { get; set; }

    /// <summary>Optional case-insensitive substring filter on the person's name.</summary>
    public string? Name { get; set; }

    /// <summary>Optional case-insensitive substring filter on the person's email.</summary>
    public string? Email { get; set; }

    /// <summary>When <c>true</c>, logically deleted persons are included in the results (FR-PE-08).</summary>
    public bool IncludeDeleted { get; set; }

    public long ActingPersonId { get; set; }

    public int ActingRole { get; set; }
}
