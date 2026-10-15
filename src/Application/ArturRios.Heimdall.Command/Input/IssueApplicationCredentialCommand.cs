using System.Text.Json.Serialization;
using ArturRios.Heimdall.Shared.Security;
using ArturRios.Mediator.Command;

namespace ArturRios.Heimdall.Command.Input;

/// <summary>
///     Issues a secret an application can exchange for an application token (UC-53, FR-AP-10).
///     The route supplies the scope and the application; the body only the optional expiry.
/// </summary>
public class IssueApplicationCredentialCommand : BaseCommand, IActorScoped
{
    /// <summary>The scope's <c>PublicId</c>, from the route.</summary>
    [JsonIgnore]
    public Guid ScopeId { get; set; }

    /// <summary>The application's <c>PublicId</c>, from the route.</summary>
    [JsonIgnore]
    public Guid Id { get; set; }

    /// <summary>When the credential stops being accepted; omitted for never. Must be in the future.</summary>
    public DateTime? ExpiresAt { get; set; }

    [JsonIgnore]
    public Guid ActingPersonId { get; set; }

    [JsonIgnore]
    public int ActingRole { get; set; }
}
