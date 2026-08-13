using System.Text.Json.Serialization;
using ArturRios.Heimdall.Shared.Security;
using ArturRios.Mediator.Query;

namespace ArturRios.Heimdall.Query.Input;

/// <summary>
///     Request to list the <c>User</c> persons of a scope, with pagination and optional filtering
///     (UC-07, FR-PE-04). <see cref="ScopeId" /> comes from the route;
///     <see cref="ActingPersonId" />/<see cref="ActingRole" /> are set by the controller from the
///     authenticated caller and are never taken from the request. All three are
///     <c>[JsonIgnore]</c>, which <c>ServerPopulatedBindingMetadataProvider</c> turns into
///     "not bindable", so they never reach the public contract.
/// </summary>
public class ListScopePersonsQuery : BaseQuery, IActorScoped
{
    /// <summary>Public identifier of the scope whose Users are listed (assigned from the route).</summary>
    [JsonIgnore]
    public Guid ScopeId { get; set; }

    /// <summary>Optional case-insensitive substring filter on the person's name.</summary>
    public string? Name { get; set; }

    /// <summary>Optional case-insensitive substring filter on the person's email.</summary>
    public string? Email { get; set; }

    /// <summary>When <c>true</c>, logically deleted persons are included in the results (FR-PE-08).</summary>
    public bool IncludeDeleted { get; set; }

    [JsonIgnore]
    public Guid ActingPersonId { get; set; }

    [JsonIgnore]
    public int ActingRole { get; set; }
}
