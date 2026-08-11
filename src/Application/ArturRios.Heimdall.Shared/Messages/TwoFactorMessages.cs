namespace ArturRios.Heimdall.Shared.Messages;

/// <summary>
///     Canonical messages produced by the two-factor authentication use cases (UC-36 – UC-40). Each
///     is mapped to an HTTP status code in <see cref="TwoFactorMessageMap" />. UC-36 and UC-37's
///     messages exist so far — UC-38 through UC-40 add their own as they are implemented.
/// </summary>
public static class TwoFactorMessages
{
    /// <summary>
    ///     UC-36 main flow and AF-36d: setup was initiated (or a pending setup was overwritten).
    ///     Nothing is active yet — UC-37 finishes the job.
    /// </summary>
    public const string SetupInitiated = "Two-factor authentication setup initiated.";

    /// <summary>
    ///     AF-36a and AF-37d: the caller already has an active two-factor configuration — refused
    ///     whether the caller tried to re-initiate setup (UC-36) or confirm it again (UC-37).
    /// </summary>
    public const string AlreadyActive = "Two-factor authentication is already active.";

    /// <summary>
    ///     AF-36b: the caller could not be resolved as a person eligible for two-factor authentication
    ///     — a Google User, or a bearer token naming no live person at all (FR-2F-01).
    /// </summary>
    public const string NotEligible = "You are not eligible to enable two-factor authentication.";

    /// <summary>AF-36c: neither the App nor the Email method was selected.</summary>
    public const string NoMethodSelected = "Select at least one two-factor method: App, Email, or both.";

    /// <summary>
    ///     UC-37 main flow: every required code checked out, so setup is now active and ten recovery
    ///     codes were issued in plaintext (FR-2F-04, FR-2F-05).
    /// </summary>
    public const string SetupConfirmed = "Two-factor authentication setup confirmed.";

    /// <summary>
    ///     AF-37a: no pending (or active) two-factor configuration exists for the caller — the same
    ///     shape as UC-36's AF-36b covers a Google User or a bearer token naming no live person, since
    ///     neither could ever hold a <c>TwoFactorAuth</c> row to confirm.
    /// </summary>
    public const string NoPendingSetup = "No pending two-factor authentication setup exists.";

    /// <summary>AF-37b: the App method is enabled and the submitted <c>appCode</c> is missing or incorrect.</summary>
    public const string AppCodeInvalid = "The authenticator app code is missing or incorrect.";

    /// <summary>
    ///     AF-37c: the Email method is enabled and the submitted <c>emailCode</c> is missing,
    ///     incorrect, expired, or already used.
    /// </summary>
    public const string EmailCodeInvalid = "The email code is missing, incorrect, expired, or already used.";
}
