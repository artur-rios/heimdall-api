using System.Text.Json.Serialization;
using ArturRios.Heimdall.Shared.Security;
using ArturRios.Mediator.Command;

namespace ArturRios.Heimdall.Command.Input;

/// <summary>
///     Intent to open or close self-registration for a scope (UC-47 toggle, FR-SR-02). Shaped exactly
///     like <see cref="SetGoogleSignInCommand" />, for the same reasons: the scope comes from the route,
///     the actor from the token, and <see cref="Enabled" /> is nullable so a body omitting it is a 400
///     rather than a silent close.
/// </summary>
public class SetSelfRegistrationCommand : BaseCommand, IActorScoped
{
    [JsonIgnore]
    public Guid Id { get; set; }

    public bool? Enabled { get; set; }

    [JsonIgnore]
    public Guid ActingPersonId { get; set; }

    [JsonIgnore]
    public int ActingRole { get; set; }
}
