using ArturRios.IdentityManager.Shared.Security;
using ArturRios.Mediator.Query;

namespace ArturRios.IdentityManager.Query.Input;

/// <summary>
///     Request to list the Google Users of a scope, with pagination and optional filtering (UC-27,
///     FR-GO-14). <see cref="ScopeId" /> comes from the route;
///     <see cref="ActingPersonId" />/<see cref="ActingRole" /> are set by the controller from the
///     authenticated caller and are never taken from the request.
/// </summary>
public class ListScopeGoogleUsersQuery : BaseQuery, IActorScoped
{
    /// <summary>Public identifier of the scope whose Google Users are listed.</summary>
    public Guid ScopeId { get; set; }

    /// <summary>Optional case-insensitive substring filter on the Google User's name.</summary>
    public string? Name { get; set; }

    /// <summary>Optional case-insensitive substring filter on the Google User's email.</summary>
    public string? Email { get; set; }

    /// <summary>When <c>true</c>, logically deleted Google Users are included (FR-GO-17).</summary>
    public bool IncludeDeleted { get; set; }

    public Guid ActingPersonId { get; set; }

    public int ActingRole { get; set; }
}
