using System.Text.Json.Serialization;
using ArturRios.Heimdall.Shared.Security;
using ArturRios.Mediator.Query;

namespace ArturRios.Heimdall.Query.Input;

/// <summary>
///     Request to list the caller's own open scope owner invitations (UC-59, FR-SC-16), soonest to expire first.
///     The caller comes from the bearer token; nothing else is accepted but the page.
/// </summary>
public class ListMyScopeInvitationsQuery : BaseQuery, IActorScoped
{
    [JsonIgnore]
    public Guid ActingPersonId { get; set; }

    [JsonIgnore]
    public int ActingRole { get; set; }
}
