using ArturRios.IdentityManager.Shared.Security;
using ArturRios.Mediator.Query;

namespace ArturRios.IdentityManager.Query.Input;

/// <summary>
///     Request to retrieve a single person by their <c>PublicId</c> (UC-07, FR-PE-03). The pagination
///     members inherited from <see cref="BaseQuery" /> are unused for a by-id lookup.
///     <see cref="ActingPersonId" />/<see cref="ActingRole" /> are set by the controller from the
///     authenticated caller, for the AF-07b visibility rule.
/// </summary>
public class GetPersonByIdQuery : BaseQuery, IActorScoped
{
    /// <summary>Public identifier of the person to retrieve.</summary>
    public Guid Id { get; set; }

    /// <summary>When <c>true</c>, a logically deleted person is still returned (FR-PE-08).</summary>
    public bool IncludeDeleted { get; set; }

    public Guid ActingPersonId { get; set; }

    public int ActingRole { get; set; }
}
