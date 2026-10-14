using System.Text.Json.Serialization;
using ArturRios.Heimdall.Shared.Security;
using ArturRios.Mediator.Query;

namespace ArturRios.Heimdall.Query.Input;

/// <summary>
///     Request to list a scope's self-registrations awaiting confirmation (UC-49, FR-SR-11), newest
///     first. <see cref="ScopeId" /> comes from the route and the actor from the bearer token; none of
///     the three is bound from the query string.
/// </summary>
public class ListPendingRegistrationsQuery : BaseQuery, IActorScoped
{
    /// <summary>Public id of the scope whose registrations are listed (from the route).</summary>
    [JsonIgnore]
    public Guid ScopeId { get; set; }

    [JsonIgnore]
    public Guid ActingPersonId { get; set; }

    [JsonIgnore]
    public int ActingRole { get; set; }
}
