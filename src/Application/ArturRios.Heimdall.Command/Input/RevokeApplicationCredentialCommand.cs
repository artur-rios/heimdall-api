using System.Text.Json.Serialization;
using ArturRios.Heimdall.Shared.Security;
using ArturRios.Mediator.Command;

namespace ArturRios.Heimdall.Command.Input;

/// <summary>
///     Revokes one of an application's credentials (UC-55, FR-AP-12). Every field comes from the
///     route or the actor.
/// </summary>
public class RevokeApplicationCredentialCommand : BaseCommand, IActorScoped
{
    /// <summary>The scope's <c>PublicId</c>, from the route.</summary>
    [JsonIgnore]
    public Guid ScopeId { get; set; }

    /// <summary>The application's <c>PublicId</c>, from the route.</summary>
    [JsonIgnore]
    public Guid Id { get; set; }

    /// <summary>The credential's <c>PublicId</c>, from the route.</summary>
    [JsonIgnore]
    public Guid CredentialId { get; set; }

    [JsonIgnore]
    public Guid ActingPersonId { get; set; }

    [JsonIgnore]
    public int ActingRole { get; set; }
}
