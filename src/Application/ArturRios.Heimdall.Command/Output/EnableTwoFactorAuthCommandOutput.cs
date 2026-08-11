using ArturRios.Mediator.Command;

namespace ArturRios.Heimdall.Command.Output;

/// <summary>
///     The result of initiating two-factor authentication setup (UC-36). Nothing is active yet — the
///     caller confirms with the returned pending methods in UC-37.
/// </summary>
public class EnableTwoFactorAuthCommandOutput : CommandOutput
{
    /// <summary>
    ///     The <c>otpauth://</c> provisioning URI for QR scanning, present only when the App method
    ///     was selected (FR-2F-02). The underlying secret is never returned in plaintext again.
    /// </summary>
    public string? OtpAuthUri { get; set; }

    /// <summary>Whether a 6-digit email code was sent, present only when the Email method was selected.</summary>
    public bool? EmailCodeSent { get; set; }
}
