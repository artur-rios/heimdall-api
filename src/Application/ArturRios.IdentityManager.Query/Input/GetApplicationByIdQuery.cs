using ArturRios.IdentityManager.Shared.Security;
using ArturRios.Mediator.Query;

namespace ArturRios.IdentityManager.Query.Input;

/// <summary>
///     Request to retrieve a single application by its <c>PublicId</c> within a scope (UC-17,
///     FR-AP-04). The pagination members inherited from <see cref="BaseQuery" /> are unused for a
///     by-id lookup. <see cref="ActingPersonId" />/<see cref="ActingRole" /> are set by the
///     controller from the authenticated caller, for the AF-17b visibility rule.
/// </summary>
public class GetApplicationByIdQuery : BaseQuery, IActorScoped
{
    /// <summary>Public identifier of the scope the application must belong to (from the route).</summary>
    public Guid ScopeId { get; set; }

    /// <summary>Public identifier of the application to retrieve.</summary>
    public Guid Id { get; set; }

    /// <summary>When <c>true</c>, a logically deleted application is still returned (FR-AP-09).</summary>
    public bool IncludeDeleted { get; set; }

    public Guid ActingPersonId { get; set; }

    public int ActingRole { get; set; }
}
