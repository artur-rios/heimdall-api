using System.Text.Json.Serialization;
using ArturRios.Heimdall.Shared.Security;
using ArturRios.Mediator.Command;

namespace ArturRios.Heimdall.Command.Input;

/// <summary>
///     Intent to set the base links a scope's verification and password-reset emails point at. Shaped
///     like <see cref="SetSelfRegistrationCommand" />: the scope comes from the route and the actor
///     from the token.
/// </summary>
/// <remarks>
///     This is its own command, not two more fields of <see cref="UpdateScopeCommand" />, because that
///     command replaces every field: a client that predates these links and does not send them would
///     erase them on its next update. Here the body is the whole state of the two links, so each one a
///     request omits, sends as <c>null</c> or leaves empty is cleared, and the global setting applies
///     to that email again.
/// </remarks>
public class SetScopeEmailLinksCommand : BaseCommand, IActorScoped
{
    [JsonIgnore]
    public Guid Id { get; set; }

    /// <summary>Base link for the scope's verification emails, or <c>null</c>/empty to clear it.</summary>
    public string? EmailVerificationUrl { get; set; }

    /// <summary>Base link for the scope's password-reset emails, or <c>null</c>/empty to clear it.</summary>
    public string? PasswordResetUrl { get; set; }

    [JsonIgnore]
    public Guid ActingPersonId { get; set; }

    [JsonIgnore]
    public int ActingRole { get; set; }
}
