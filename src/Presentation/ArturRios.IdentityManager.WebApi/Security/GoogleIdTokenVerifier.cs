using ArturRios.IdentityManager.Command.Services;
using Google.Apis.Auth;

namespace ArturRios.IdentityManager.WebApi.Security;

/// <summary>
///     Verifies UC-25's Google ID tokens against Google (FR-GO-11, NFR-13).
///     <see cref="GoogleJsonWebSignature.ValidateAsync" /> checks the signature against Google's
///     published keys, the issuer against <c>accounts.google.com</c>, and the expiry; the configured
///     client IDs constrain the audience, so a token minted for someone else's application is
///     refused even though Google signed it.
/// </summary>
/// <remarks>
///     Every failure becomes <c>null</c> rather than an exception, because AF-25a treats them alike
///     and the handler has no decision to make between "expired" and "forged". The reason is logged
///     at debug level so an operator can still tell them apart; it is deliberately not returned to
///     the caller.
/// </remarks>
public class GoogleIdTokenVerifier(
    GoogleSignInOptions options,
    ILogger<GoogleIdTokenVerifier> logger) : IGoogleIdTokenVerifier
{
    public async Task<GoogleIdTokenPayload?> VerifyAsync(string idToken)
    {
        if (string.IsNullOrWhiteSpace(idToken))
        {
            return null;
        }

        try
        {
            var payload = await GoogleJsonWebSignature.ValidateAsync(
                idToken,
                new GoogleJsonWebSignature.ValidationSettings { Audience = options.ClientIds });

            return new GoogleIdTokenPayload(
                payload.Subject,
                payload.Email,
                payload.EmailVerified,
                payload.Name,
                payload.Picture);
        }
        catch (InvalidJwtException exception)
        {
            logger.LogDebug(exception, "Google ID token rejected");

            return null;
        }
    }
}
