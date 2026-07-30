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
}
