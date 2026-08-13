using System.Text.Json.Serialization;
using ArturRios.Heimdall.Shared.Security;
using ArturRios.Mediator.Query;

namespace ArturRios.Heimdall.Query.Input;

/// <summary>
///     Request to list the permissions of a scope, with pagination and optional filtering (UC-32,
///     FR-SP-05). <see cref="ScopeId" /> comes from the route;
///     <see cref="ActingPersonId" />/<see cref="ActingRole" /> are set by the controller from the
///     authenticated caller and are never taken from the request — a Scope Admin sees only the
///     permissions of a scope they own, so a forged acting id would be a forged answer. A scope
///     permission has no owner of its own, so there is no owner filter. All three are
///     <c>[JsonIgnore]</c>, which <c>ServerPopulatedBindingMetadataProvider</c> turns into
///     "not bindable", so they never reach the public contract.
/// </summary>
public class ListScopePermissionsQuery : BaseQuery, IActorScoped
{
    /// <summary>Public identifier of the scope whose permissions are listed (assigned from the route).</summary>
    [JsonIgnore]
    public Guid ScopeId { get; set; }

    /// <summary>Optional case-insensitive substring filter on the permission's name.</summary>
    public string? Name { get; set; }

    /// <summary>When <c>true</c>, logically deleted permissions are included (FR-SP-09).</summary>
    public bool IncludeDeleted { get; set; }

    [JsonIgnore]
    public Guid ActingPersonId { get; set; }

    [JsonIgnore]
    public int ActingRole { get; set; }
}
