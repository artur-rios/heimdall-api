using System.Text.Json.Serialization;
using ArturRios.Heimdall.Shared.Security;
using ArturRios.Mediator.Query;

namespace ArturRios.Heimdall.Query.Input;

/// <summary>
///     Request to list scopes with pagination and optional filtering (UC-02 read b, FR-SC-03). Page
///     number and size are inherited from <see cref="BaseQuery" />.
///     <see cref="ActingPersonId" />/<see cref="ActingRole" /> are set by the controller from the
///     authenticated caller and are never taken from the request; both are <c>[JsonIgnore]</c>,
///     which <c>ServerPopulatedBindingMetadataProvider</c> turns into "not bindable", so they never
///     reach the public contract.
/// </summary>
public class ListScopesQuery : BaseQuery, IActorScoped
{
    /// <summary>Optional case-insensitive substring filter on the scope name.</summary>
    public string? Name { get; set; }

    /// <summary>When <c>true</c>, logically deleted scopes are included in the results (FR-SC-07).</summary>
    public bool IncludeDeleted { get; set; }

    [JsonIgnore]
    public Guid ActingPersonId { get; set; }

    [JsonIgnore]
    public int ActingRole { get; set; }
}
