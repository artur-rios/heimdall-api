using ArturRios.Heimdall.Shared.Security;
using ArturRios.Mediator.Query;

namespace ArturRios.Heimdall.Query.Input;

/// <summary>
///     Request to retrieve a single scope permission by its <c>PublicId</c> within a scope (UC-32,
///     FR-SP-04). The pagination members inherited from <see cref="BaseQuery" /> are unused for a
///     by-id lookup. <see cref="ActingPersonId" />/<see cref="ActingRole" /> are set by the
///     controller from the authenticated caller, for the AF-32e scope-ownership rule.
/// </summary>
public class GetScopePermissionByIdQuery : BaseQuery, IActorScoped
{
    /// <summary>Public identifier of the scope the permission must belong to (from the route).</summary>
    public Guid ScopeId { get; set; }

    /// <summary>Public identifier of the scope permission to retrieve.</summary>
    public Guid Id { get; set; }

    /// <summary>When <c>true</c>, a logically deleted permission is still returned (FR-SP-09).</summary>
    public bool IncludeDeleted { get; set; }

    public Guid ActingPersonId { get; set; }

    public int ActingRole { get; set; }
}
