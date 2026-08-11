namespace ArturRios.Heimdall.Shared.Messages;

/// <summary>
///     Canonical messages produced by the two-factor authentication use cases (UC-36 – UC-40). Each
///     is mapped to an HTTP status code in <see cref="TwoFactorMessageMap" />. Only UC-36's messages
///     exist so far — UC-37 through UC-40 add their own as they are implemented.
/// </summary>
public static class TwoFactorMessages
{
    /// <summary>
    ///     UC-36 main flow and AF-36d: setup was initiated (or a pending setup was overwritten).
    ///     Nothing is active yet — UC-37 finishes the job.
    /// </summary>
    public const string SetupInitiated = "Two-factor authentication setup initiated.";

    /// <summary>AF-36a: the caller already has an active two-factor configuration.</summary>
    public const string AlreadyActive = "Two-factor authentication is already active.";

    /// <summary>
    ///     AF-36b: the caller could not be resolved as a person eligible for two-factor authentication
    ///     — a Google User, or a bearer token naming no live person at all (FR-2F-01).
    /// </summary>
    public const string NotEligible = "You are not eligible to enable two-factor authentication.";

    /// <summary>AF-36c: neither the App nor the Email method was selected.</summary>
    public const string NoMethodSelected = "Select at least one two-factor method: App, Email, or both.";
}
