namespace ArturRios.Heimdall.Shared.Messages;

/// <summary>
///     Canonical messages produced by the two-factor authentication use cases (UC-36 – UC-40). Each
///     is mapped to an HTTP status code in <see cref="TwoFactorMessageMap" />.
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

    /// <summary>
    ///     UC-38 main flow: the challenge token was valid and a second factor checked out, so the
    ///     full authentication token was issued (FR-2F-09).
    /// </summary>
    public const string VerificationSuccessful = "Two-factor verification successful.";

    /// <summary>
    ///     AF-38a: the challenge token is missing, malformed, not signed by this API, expired, or
    ///     does not carry the MFA-pending claim (FR-2F-10).
    /// </summary>
    public const string ChallengeTokenInvalid = "The two-factor challenge is invalid or has expired. Log in again.";

    /// <summary>
    ///     UC-38 step 5 (FR-2F-09): the second factor checked out, but the person's scope eligibility —
    ///     re-checked exactly as UC-11's own AF-11d/AF-11e do — no longer holds by the time UC-38 runs
    ///     (e.g. their scope was logically deleted between the UC-11 password check and this
    ///     completion). Kept distinct from <see cref="ChallengeTokenInvalid" /> since the token itself
    ///     was valid and the factor was genuinely correct; the caller is told to log in again for the
    ///     same reason UC-11 itself would now refuse them.
    /// </summary>
    public const string ScopeNoLongerEligible = "This account is no longer eligible to sign in. Log in again.";

    /// <summary>
    ///     AF-38b and AF-38c: neither a valid app/email code nor an unused recovery code was
    ///     submitted. Deliberately the same message for a wrong/missing code and an already-used
    ///     recovery code (AF-38c), so a caller cannot tell a reused recovery code from one that was
    ///     never real — the same reasoning UC-11's AF-11a…AF-11e collapse into one message for.
    /// </summary>
    public const string FactorInvalid = "The code or recovery code is missing, incorrect, or already used.";

    /// <summary>
    ///     UC-39 main flow: both the password and the second factor checked out, so the
    ///     <c>TWO_FACTOR_AUTH</c> row and its recovery codes were permanently removed (FR-2F-11).
    /// </summary>
    public const string Disabled = "Two-factor authentication has been disabled.";

    /// <summary>
    ///     AF-39a: two-factor authentication is not active for the caller — including the edge case
    ///     where the caller cannot be resolved as a live person at all (a Google User, or a bearer
    ///     token naming a person since hard deleted), which could never hold an active row either.
    /// </summary>
    public const string NotActive = "Two-factor authentication is not active for this account.";

    /// <summary>AF-39b: the submitted password does not match the caller's current password.</summary>
    public const string PasswordMismatch = "The current password is incorrect.";

    /// <summary>
    ///     UC-40 main flow: the second factor checked out, so every existing recovery code was
    ///     replaced and ten new ones were issued in plaintext (FR-2F-12). AF-40a and AF-40b reuse
    ///     <see cref="NotActive" /> and <see cref="FactorInvalid" /> respectively — UC-40's own
    ///     preconditions are exactly UC-39's "not active" check and UC-38's second-factor check, so no
    ///     new refusal message is invented for either.
    /// </summary>
    public const string RecoveryCodesRegenerated = "Recovery codes have been regenerated.";

    /// <summary>
    ///     FR-2F-15 main flow: the caller's own two-factor status was read. Returned whether or not
    ///     any configuration exists — "never enabled" is the ordinary state of most accounts and is
    ///     reported as a success with every flag false, not as a refusal.
    /// </summary>
    public const string StatusRetrieved = "Two-factor authentication status retrieved.";
}
