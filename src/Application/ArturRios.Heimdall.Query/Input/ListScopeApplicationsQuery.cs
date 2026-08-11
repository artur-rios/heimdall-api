using ArturRios.Heimdall.Shared.Security;
using ArturRios.Mediator.Query;

namespace ArturRios.Heimdall.Query.Input;

/// <summary>
///     Request to list the applications of a scope, with pagination and optional filtering (UC-17,
///     FR-AP-05). <see cref="ScopeId" /> comes from the route;
///     <see cref="ActingPersonId" />/<see cref="ActingRole" /> are set by the controller from the
///     authenticated caller and are never taken from the request — a Scope Admin sees only the
///     applications they own, so a forged acting id would be a forged answer.
/// </summary>
public class ListScopeApplicationsQuery : BaseQuery, IActorScoped
{
    /// <summary>Public identifier of the scope whose applications are listed.</summary>
    public Guid ScopeId { get; set; }

    /// <summary>Optional case-insensitive substring filter on the application's name.</summary>
    public string? Name { get; set; }

    /// <summary>
    ///     Optional filter on the owner's <c>PublicId</c>. Useful to a System Admin narrowing a busy
    ///     scope; inert for a Scope Admin, whose results are already restricted to their own.
    /// </summary>
    public Guid? OwnerId { get; set; }

    /// <summary>When <c>true</c>, logically deleted applications are included (FR-AP-09).</summary>
    public bool IncludeDeleted { get; set; }

    public Guid ActingPersonId { get; set; }

    public int ActingRole { get; set; }
}
