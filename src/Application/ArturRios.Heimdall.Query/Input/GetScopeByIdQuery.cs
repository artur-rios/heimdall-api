using ArturRios.Heimdall.Shared.Security;
using ArturRios.Mediator.Query;

namespace ArturRios.Heimdall.Query.Input;

/// <summary>
///     Request to retrieve a single scope by its <c>PublicId</c> (UC-02, FR-SC-02). The pagination
///     members inherited from <see cref="BaseQuery" /> are unused for a by-id lookup.
///     <see cref="ActingPersonId" />/<see cref="ActingRole" /> are set by the controller from the
///     authenticated caller and are never taken from the request.
/// </summary>
public class GetScopeByIdQuery : BaseQuery, IActorScoped
{
    /// <summary>Public identifier of the scope to retrieve.</summary>
    public Guid Id { get; set; }

    /// <summary>When <c>true</c>, a logically deleted scope is still returned (FR-SC-07).</summary>
    public bool IncludeDeleted { get; set; }

    public Guid ActingPersonId { get; set; }

    public int ActingRole { get; set; }
}
