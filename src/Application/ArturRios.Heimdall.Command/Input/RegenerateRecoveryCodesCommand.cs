using System.Text.Json.Serialization;
using ArturRios.Heimdall.Shared.Security;
using ArturRios.Mediator.Command;

namespace ArturRios.Heimdall.Command.Input;

/// <summary>
///     Intent to invalidate the caller's current recovery codes and issue a fresh set of ten (UC-40,
///     FR-2F-12), requiring a valid second factor — an app/email code or one of the caller's
///     remaining recovery codes — verified exactly as UC-38 verifies one. Unlike
///     <see cref="DisableTwoFactorAuthCommand" />, no password is required — the Use Case
///     Specification Document's precondition for UC-40 is only that two-factor authentication is
///     active, and the second factor alone is what UC-38 already treats as sufficient proof of
///     possession. <see cref="ActingPersonId" />/<see cref="ActingRole" /> are set by the controller
///     from the authenticated caller and are never bound from the body — a caller can only ever
///     regenerate their own recovery codes. They are <c>[JsonIgnore]</c>, so they are not
///     deserialized from the body and do not appear in the request schema.
/// </summary>
public class RegenerateRecoveryCodesCommand : BaseCommand, IActorScoped
{
    /// <summary>The caller's current app or email code, when proving a code rather than a recovery code.</summary>
    public string? Code { get; set; }

    /// <summary>One of the caller's unused recovery codes, when no code is available.</summary>
    public string? RecoveryCode { get; set; }

    [JsonIgnore]
    public Guid ActingPersonId { get; set; }

    [JsonIgnore]
    public int ActingRole { get; set; }
}
