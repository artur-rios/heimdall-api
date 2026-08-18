using System.Text.Json.Serialization;
using ArturRios.Heimdall.Shared.Security;
using ArturRios.Mediator.Query;

namespace ArturRios.Heimdall.Query.Input;

/// <summary>
///     Request to list the system's <c>ScopeAdmin</c> persons, with pagination and optional filtering
///     (UC-07 read d, FR-PE-12). This is what backs an owner picker: UI-11 selects a scope's first
///     owners before the scope exists, and UI-14 adds an existing Scope Admin as a co-owner.
///     <see cref="ActingPersonId" />/<see cref="ActingRole" /> are set by the controller from the
///     authenticated caller and are never taken from the request; both are <c>[JsonIgnore]</c>,
///     which <c>ServerPopulatedBindingMetadataProvider</c> turns into "not bindable", so they never
///     reach the public contract.
/// </summary>
/// <remarks>
///     There is deliberately no <c>IncludeDeleted</c>. A logically deleted administrator is never a
///     valid owner — <c>PersonNotValidScopeAdmin</c> refuses one — so listing them could only offer
///     a picker entry whose submission is guaranteed to fail.
/// </remarks>
public class ListScopeAdminsQuery : BaseQuery, IActorScoped
{
    /// <summary>Optional case-insensitive substring filter on the administrator's name.</summary>
    public string? Name { get; set; }

    /// <summary>Optional case-insensitive substring filter on the administrator's email.</summary>
    public string? Email { get; set; }

    /// <summary>
    ///     When set, the current owners of this scope are removed from the results (UI-14 AF-14c).
    ///     The exclusion runs before pagination, so a page is not silently short. The caller must be
    ///     entitled to manage the named scope: without that check, running the query with and
    ///     without this parameter and diffing the two results would enumerate the owners of any
    ///     scope, which is exactly what this endpoint's minimal projection exists to prevent.
    /// </summary>
    public Guid? ExcludeOwnersOfScopeId { get; set; }

    [JsonIgnore]
    public Guid ActingPersonId { get; set; }

    [JsonIgnore]
    public int ActingRole { get; set; }
}
