using System.Text.Json.Serialization;
using ArturRios.Heimdall.Shared.Security;
using ArturRios.Mediator.Command;

namespace ArturRios.Heimdall.Command.Input;

/// <summary>
///     A data subject's request for a copy of everything held about them (UC-41, GDPR Art. 15 and
///     20, LGPD Art. 18 II and V).
/// </summary>
/// <remarks>
///     <para>
///         The subject is the authenticated caller, always. There is no identifier in the path or
///         the body, so the endpoint has no shape in which one identity could export another's data.
///     </para>
///     <para>
///         <b>Why a command for something that reads nothing.</b> Two reasons, and neither is
///         tidiness. An export is the single highest-value action a stolen token can perform — it
///         discloses everything about a person in one response — so it has to reach the audit trail,
///         and in this codebase only commands are audited. And a <c>GET</c> would invite caching: a
///         document containing everything about a person is exactly what must never sit in a proxy
///         or a browser cache. Both point the same way.
///     </para>
/// </remarks>
public class ExportMyDataCommand : BaseCommand, IActorScoped
{
    [JsonIgnore]
    public Guid ActingPersonId { get; set; }

    [JsonIgnore]
    public int ActingRole { get; set; }
}
