namespace ArturRios.IdentityManager.Command.Services;

/// <summary>
///     The claims UC-25 trusts once a Google ID token has been verified (UC-25 step 3, FR-GO-11).
///     Exactly the five fields a Google User is populated from (FR-GO-05) — nothing else on the
///     token is read, so the application layer never sees an unverified claim.
/// </summary>
/// <param name="Subject">Google's stable <c>sub</c> claim, stored as the Google User's <c>GoogleId</c>.</param>
/// <param name="Email">The <c>email</c> claim.</param>
/// <param name="EmailVerified">The <c>email_verified</c> claim.</param>
/// <param name="Name">The <c>name</c> claim, absent on tokens whose issuer withheld the profile scope.</param>
/// <param name="PictureUrl">The <c>picture</c> claim; optional, as the Google User field is.</param>
public record GoogleIdTokenPayload(
    string Subject,
    string Email,
    bool EmailVerified,
    string? Name,
    string? PictureUrl);

/// <summary>
///     Verifies a Google ID token and returns the claims it carries (UC-25 step 3, FR-GO-11,
///     NFR-13). The signing scheme, the trusted issuers, and the accepted audiences belong to the
///     presentation layer, so the application layer only says which token it wants checked.
/// </summary>
/// <remarks>
///     The same arrangement as <see cref="IAuthTokenIssuer" />, and for the same reason: UC-25's
///     handler is a use case, not a JWT library, and stays unit-testable without one.
/// </remarks>
public interface IGoogleIdTokenVerifier
{
    /// <param name="idToken">The raw ID token the caller presented.</param>
    /// <returns>
    ///     The verified claims, or <c>null</c> if the token is absent, malformed, expired, signed by
    ///     someone else, or issued for another audience. A single <c>null</c> for every failure
    ///     because AF-25a treats them alike — the handler has no decision to make between them.
    /// </returns>
    Task<GoogleIdTokenPayload?> VerifyAsync(string idToken);
}
