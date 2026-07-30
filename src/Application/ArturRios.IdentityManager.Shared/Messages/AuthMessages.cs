namespace ArturRios.IdentityManager.Shared.Messages;

/// <summary>
///     Canonical messages produced by the authentication use cases. Each is mapped to an HTTP status
///     code in <see cref="AuthMessageMap" />.
/// </summary>
public static class AuthMessages
{
    /// <summary>UC-11 success: the person was authenticated and a token issued.</summary>
    public const string LoginSuccessful = "Login successful.";

    /// <summary>
    ///     UC-11 AF-11a…AF-11e: the credentials were not accepted. Deliberately one message for all
    ///     five conditions — unknown person, wrong password, deleted person, deleted scope, and a
    ///     Scope Admin whose every owned scope is deleted — so a caller cannot use the login
    ///     endpoint to discover which emails exist, which accounts are deleted, or which scopes are
    ///     gone.
    /// </summary>
    public const string InvalidCredentials = "Invalid credentials.";

    /// <summary>AF-11f: email was not supplied.</summary>
    public const string EmailRequired = "Email is required.";

    /// <summary>AF-11f: email is not a valid address.</summary>
    public const string EmailInvalid = "Email is not valid.";

    /// <summary>AF-11f: password was not supplied.</summary>
    public const string PasswordRequired = "Password is required.";

    /// <summary>
    ///     UC-12 main flow and AF-12a: the recovery request was accepted. Deliberately the same
    ///     message whether or not the email belongs to anyone — a caller who cannot already log in
    ///     learns nothing about which addresses are registered.
    /// </summary>
    public const string PasswordRecoveryRequested = "If the email exists, a reset link has been sent.";

    /// <summary>UC-13 success: the password was changed and the token consumed.</summary>
    public const string PasswordResetSuccessful = "Password reset successfully.";

    /// <summary>
    ///     AF-13c and AF-14c: no token matches the one supplied. Unlike UC-11 and UC-12, the two
    ///     token-spending use cases name each rejection separately, and there is nothing to hide by
    ///     doing so: the value is a 48-character random string, so a caller holding one already knows
    ///     it was issued to them, and a caller guessing learns only that their guess was wrong.
    /// </summary>
    /// <remarks>
    ///     Shared by UC-13 and UC-14 because both specify the same wording. <see cref="AuthMessageMap" />
    ///     is keyed by the message string, so a second constant holding the same value could not be
    ///     mapped to a status code at all.
    /// </remarks>
    public const string TokenInvalid = "Invalid token.";

    /// <summary>
    ///     AF-13a and AF-14a: the token was issued but its lifetime has run out (FR-PR-04, FR-EV-02).
    /// </summary>
    public const string TokenExpired = "Token expired.";

    /// <summary>
    ///     AF-13b and AF-14b: the token has already been spent — on a password reset (FR-PR-04) or an
    ///     email verification (FR-EV-03) — and cannot be spent twice.
    /// </summary>
    public const string TokenAlreadyUsed = "Token already used.";

    /// <summary>AF-13d, and UC-14's input validation (NFR-10): the token was not supplied.</summary>
    public const string TokenRequired = "Token is required.";

    /// <summary>
    ///     AF-13d: the new password is shorter than the minimum. UC-11 deliberately has no such rule —
    ///     a short password there is a wrong password — but UC-13 sets one, so the same floor that
    ///     applies when a person is created (UC-06) applies when their password is replaced.
    /// </summary>
    public const string PasswordTooShort = "Password must be at least 8 characters.";

    /// <summary>
    ///     UC-14 success: the address was confirmed and the token consumed (FR-EV-03). Also the answer
    ///     when the address was already verified — UC-14 defines no alternative flow for that, and the
    ///     caller's link did exactly what it promised.
    /// </summary>
    public const string EmailVerifiedSuccessfully = "Email verified.";
}
