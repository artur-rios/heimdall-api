using System.Text.Json.Serialization;
using ArturRios.Heimdall.Shared.Security;
using ArturRios.Mediator.Query;

namespace ArturRios.Heimdall.Query.Input;

/// <summary>
///     Request to list the system's <c>SystemAdmin</c> persons, with pagination and optional
///     filtering (UC-07 read e, FR-PE-13), so a System Admin can see who else holds full access to
///     the deployment. <see cref="ActingPersonId" />/<see cref="ActingRole" /> are set by the
///     controller from the authenticated caller and are never taken from the request; both are
///     <c>[JsonIgnore]</c>, which <c>ServerPopulatedBindingMetadataProvider</c> turns into "not
///     bindable", so they never reach the public contract.
/// </summary>
/// <remarks>
///     There is deliberately no <c>IncludeDeleted</c>, as in <see cref="ListScopeAdminsQuery" />: the
///     question this answers is who holds full access now, and a logically deleted administrator
///     holds none.
/// </remarks>
public class ListSystemAdminsQuery : BaseQuery, IActorScoped
{
    /// <summary>Optional case-insensitive substring filter on the administrator's name.</summary>
    public string? Name { get; set; }

    /// <summary>Optional case-insensitive substring filter on the administrator's email.</summary>
    public string? Email { get; set; }

    [JsonIgnore]
    public Guid ActingPersonId { get; set; }

    [JsonIgnore]
    public int ActingRole { get; set; }
}
