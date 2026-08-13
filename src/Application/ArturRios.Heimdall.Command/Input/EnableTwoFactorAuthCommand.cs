using System.Text.Json.Serialization;
using ArturRios.Heimdall.Shared.Security;
using ArturRios.Mediator.Command;

namespace ArturRios.Heimdall.Command.Input;

/// <summary>
///     Intent to begin opting the caller into two-factor authentication (UC-36, FR-2F-01),
///     selecting an authenticator-app method, an email method, or both. <see cref="ActingPersonId" />
///     /<see cref="ActingRole" /> are set by the controller from the authenticated caller and are
///     never bound from the body — like <see cref="ResendVerificationEmailCommand" />, a caller can
///     only ever act on their own configuration. They are <c>[JsonIgnore]</c>, so they are not
///     deserialized from the body and do not appear in the request schema.
/// </summary>
public class EnableTwoFactorAuthCommand : BaseCommand, IActorScoped
{
    /// <summary>The method(s) to configure: <c>"App"</c>, <c>"Email"</c>, or both. Required, non-empty.</summary>
    public List<string> Methods { get; set; } = [];

    [JsonIgnore]
    public Guid ActingPersonId { get; set; }

    [JsonIgnore]
    public int ActingRole { get; set; }
}
