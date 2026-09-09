using System.Text.Json.Serialization;
using ArturRios.Heimdall.Shared.Security;
using ArturRios.Mediator.Command;

namespace ArturRios.Heimdall.Command.Input;

/// <summary>
///     A data subject's request to have processing of their own identity restricted (UC-44, GDPR
///     Art. 18, LGPD Art. 18 III–IV) — suspended without being deleted, while something about it is
///     disputed.
/// </summary>
/// <remarks>
///     The subject is the authenticated caller. No identifier appears in the path or the body, so
///     one identity cannot restrict another's — the same shape as UC-41 and UC-42.
/// </remarks>
public class RestrictProcessingCommand : BaseCommand, IActorScoped
{
    /// <summary>
    ///     Which of Art. 18(1)'s four grounds the restriction rests on (see
    ///     <c>RestrictionGrounds</c>).
    /// </summary>
    public int Ground { get; set; }

    [JsonIgnore]
    public Guid ActingPersonId { get; set; }

    [JsonIgnore]
    public int ActingRole { get; set; }
}
