using System.Text.Json.Serialization;
using ArturRios.Heimdall.Shared.Security;
using ArturRios.Mediator.Command;

namespace ArturRios.Heimdall.Command.Input;

/// <summary>
///     A data subject's request to have their own identity erased (UC-42, GDPR Art. 17, LGPD Art. 18
///     VI). The subject is always the authenticated caller — there is no identifier in the path or
///     the body — so one identity can never request another's erasure.
/// </summary>
/// <remarks>
///     <para>
///         Exactly one of the two credentials is used, and which one is not the caller's choice: a
///         person authenticates with their password, a Google User with a fresh Google ID token,
///         because a Google User has no password to present. The handler picks by identity type.
///     </para>
///     <para>
///         <see cref="ActingPersonId" /> and <see cref="ActingRole" /> are set by the controller from
///         the authenticated caller and never taken from the request; both are <c>[JsonIgnore]</c>,
///         which <c>ServerPopulatedBindingMetadataProvider</c> turns into "not bindable", so they
///         never reach the public contract.
///     </para>
/// </remarks>
public class RequestErasureCommand : BaseCommand, IActorScoped
{
    /// <summary>The caller's current password, when the caller is a person.</summary>
    public string? Password { get; set; }

    /// <summary>A freshly issued Google ID token, when the caller is a Google User.</summary>
    public string? IdToken { get; set; }

    [JsonIgnore]
    public Guid ActingPersonId { get; set; }

    [JsonIgnore]
    public int ActingRole { get; set; }
}
