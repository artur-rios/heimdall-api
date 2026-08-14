using ArturRios.Heimdall.Command.Services;
using Google.Apis.Auth;

namespace ArturRios.Heimdall.WebApi.Security;

/// <summary>
///     Verifies UC-25's Google ID tokens against Google (FR-GO-11, NFR-13).
///     <see cref="GoogleJsonWebSignature.ValidateAsync(string, GoogleJsonWebSignature.ValidationSettings)" />
///     checks the signature against Google's
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

            // Reached only once ValidateAsync has returned, i.e. once signature, issuer, audience
            // and expiry are all verified: the claim is never read off an untrusted token. Why the
            // claim is re-read off the token at all, rather than taken from the typed payload, is
            // GoogleIdTokenClaims.EmailVerified's business.
            return new GoogleIdTokenPayload(
                payload.Subject,
                payload.Email,
                GoogleIdTokenClaims.EmailVerified(idToken, logger),
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
