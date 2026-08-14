using System.Text.Json;
using ArturRios.Heimdall.Command.Services;
using Google.Apis.Auth;
using Microsoft.IdentityModel.Tokens;

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

            return new GoogleIdTokenPayload(
                payload.Subject,
                payload.Email,
                EmailVerified(idToken, payload),
                payload.Name,
                payload.Picture);
        }
        catch (InvalidJwtException exception)
        {
            logger.LogDebug(exception, "Google ID token rejected");

            return null;
        }
    }

    /// <summary>
    ///     The <c>email_verified</c> claim, or <c>null</c> when the token carries none.
    /// </summary>
    /// <remarks>
    ///     <see cref="GoogleJsonWebSignature.Payload.EmailVerified" /> is a non-nullable
    ///     <c>bool</c>, so a token that omitted the claim deserializes to the same <c>false</c> a
    ///     token asserting "not verified" does. The distinction matters to FR-GO-19, so the claim
    ///     set is re-read off the token's own payload segment to answer the one question the typed
    ///     payload cannot: was the claim there at all. Only presence is taken from the raw JSON —
    ///     the value still comes from the object the library validated and deserialized — and this
    ///     runs only after validation succeeded, so nothing untrusted is being parsed.
    /// </remarks>
    private static bool? EmailVerified(string idToken, GoogleJsonWebSignature.Payload payload)
    {
        var segments = idToken.Split('.');

        if (segments.Length != 3)
        {
            return null;
        }

        using var claims = JsonDocument.Parse(Base64UrlEncoder.DecodeBytes(segments[1]));

        return claims.RootElement.TryGetProperty("email_verified", out _) ? payload.EmailVerified : null;
    }
}
