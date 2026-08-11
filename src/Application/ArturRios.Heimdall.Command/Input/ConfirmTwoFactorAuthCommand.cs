using ArturRios.Heimdall.Shared.Security;
using ArturRios.Mediator.Command;

namespace ArturRios.Heimdall.Command.Input;

/// <summary>
///     Intent to confirm the caller's pending two-factor authentication setup (UC-37, FR-2F-04),
///     proving control of every method selected in UC-36. <see cref="ActingPersonId" />/
///     <see cref="ActingRole" /> are set by the controller from the authenticated caller and are
///     never bound from the body — like <see cref="EnableTwoFactorAuthCommand" />, a caller can only
///     ever confirm their own configuration.
/// </summary>
public class ConfirmTwoFactorAuthCommand : BaseCommand, IActorScoped
{
    /// <summary>The 6-digit authenticator-app code, required only when the App method is enabled.</summary>
    public string? AppCode { get; set; }

    /// <summary>The 6-digit email code, required only when the Email method is enabled.</summary>
    public string? EmailCode { get; set; }

    public Guid ActingPersonId { get; set; }

    public int ActingRole { get; set; }
}
