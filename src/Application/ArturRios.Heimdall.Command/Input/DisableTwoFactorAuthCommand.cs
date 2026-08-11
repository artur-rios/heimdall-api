using ArturRios.Heimdall.Shared.Security;
using ArturRios.Mediator.Command;

namespace ArturRios.Heimdall.Command.Input;

/// <summary>
///     Intent to turn off the caller's own two-factor authentication (UC-39, FR-2F-11), requiring
///     both the caller's current password and a valid second factor — an app/email code or a
///     recovery code — exactly as hard to satisfy as a login. <see cref="ActingPersonId" />/
///     <see cref="ActingRole" /> are set by the controller from the authenticated caller and are
///     never bound from the body — like <see cref="EnableTwoFactorAuthCommand" />, a caller can only
///     ever disable their own configuration.
/// </summary>
public class DisableTwoFactorAuthCommand : BaseCommand, IActorScoped
{
    /// <summary>The caller's current password (UC-11 step 3's check, reused here).</summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>The caller's current app or email code, when proving a code rather than a recovery code.</summary>
    public string? Code { get; set; }

    /// <summary>One of the caller's unused recovery codes, when no code is available.</summary>
    public string? RecoveryCode { get; set; }

    public Guid ActingPersonId { get; set; }

    public int ActingRole { get; set; }
}
