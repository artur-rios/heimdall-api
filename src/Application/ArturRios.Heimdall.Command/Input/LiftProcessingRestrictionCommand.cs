using System.Text.Json.Serialization;
using ArturRios.Heimdall.Shared.Security;
using ArturRios.Mediator.Command;

namespace ArturRios.Heimdall.Command.Input;

/// <summary>
///     Lifts a restriction on processing (UC-45, GDPR Art. 18(3)).
/// </summary>
/// <remarks>
///     <para>
///         Two callers, and the difference matters. A <b>System Admin</b> lifting somebody else's
///         restriction must inform them first — Art. 18(3) makes that a precondition, not a courtesy
///         afterwards. A <b>subject</b> lifting their own needs no notification: they are the person
///         who would be notified.
///     </para>
///     <para>
///         <see cref="SubjectId" /> is therefore optional. Absent, the caller is lifting their own;
///         present, they are acting on somebody else's and the role requirement applies.
///     </para>
/// </remarks>
public class LiftProcessingRestrictionCommand : BaseCommand, IActorScoped
{
    /// <summary>
    ///     The identity whose restriction is being lifted, or <c>null</c> to lift the caller's own.
    /// </summary>
    public Guid? SubjectId { get; set; }

    [JsonIgnore]
    public Guid ActingPersonId { get; set; }

    [JsonIgnore]
    public int ActingRole { get; set; }

    /// <summary>
    ///     Whether the caller's token is subject-rights-only (NFR-24) — issued to a restricted
    ///     identity at sign-in. Such a token acts for its subject alone, so it never lifts anybody
    ///     else's restriction, whatever role it claims and even once its own restriction is gone.
    ///     Taken from the token, never from the request.
    /// </summary>
    [JsonIgnore]
    public bool ActingSubjectRightsOnly { get; set; }
}
