using ArturRios.Heimdall.Command.Services;
using Google.Apis.Auth;
// Google.Apis.Auth has a JsonWebToken of its own; the alias names the one meant without hiding it.
using JsonWebToken = Microsoft.IdentityModel.JsonWebTokens.JsonWebToken;

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
    private const string EmailVerifiedClaim = "email_verified";

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
            // and expiry are all verified: the claim is never read off an untrusted token.
            return new GoogleIdTokenPayload(
                payload.Subject,
                payload.Email,
                EmailVerified(idToken),
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
    ///     The <c>email_verified</c> claim, or <c>null</c> when the token asserts nothing — the
    ///     claim is absent, is JSON <c>null</c>, or holds something that is not a boolean.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <see cref="GoogleJsonWebSignature.Payload.EmailVerified" /> is a non-nullable
    ///         <c>bool</c>, so a token that omitted the claim deserializes to the same <c>false</c>
    ///         a token asserting "not verified" does. The distinction matters to FR-GO-19, which
    ///         must leave a stored <c>true</c> alone rather than downgrade it, so the claim is read
    ///         off the token itself with
    ///         <see cref="JsonWebToken.TryGetPayloadValue{T}(string, out T)" />, which answers
    ///         presence and value in one call and leaves segment splitting and base64url decoding
    ///         to the library. It re-reads the very token
    ///         <see cref="GoogleJsonWebSignature.ValidateAsync(string, GoogleJsonWebSignature.ValidationSettings)" />
    ///         just validated, so the bytes it parses are the signed ones.
    ///     </para>
    ///     <para>
    ///         The second probe covers the one case the first gets wrong. Measured against
    ///         Microsoft.IdentityModel.JsonWebTokens 8.19.2, <c>TryGetPayloadValue&lt;bool&gt;</c>
    ///         reports an explicit <c>"email_verified": null</c> as present-and-<c>false</c>, which
    ///         would be exactly the silent downgrade FR-GO-19 forbids; the <c>bool?</c> overload
    ///         succeeds only for that null-valued claim — an absent claim and a real <c>true</c> or
    ///         <c>false</c> all make it fail — so it isolates it precisely.
    ///     </para>
    ///     <para>
    ///         Anything thrown becomes "absent" rather than escaping. The catch in
    ///         <see cref="VerifyAsync" /> only handles <see cref="InvalidJwtException" />, so an
    ///         exception from here would leave a token Google itself vouched for failing as a 500
    ///         instead of signing the caller in. "Absent" is the safe answer: it writes nothing.
    ///     </para>
    /// </remarks>
    private bool? EmailVerified(string idToken)
    {
        try
        {
            var token = new JsonWebToken(idToken);

            var present = token.TryGetPayloadValue<bool>(EmailVerifiedClaim, out var emailVerified);
            var explicitlyNull = token.TryGetPayloadValue<bool?>(EmailVerifiedClaim, out _);

            return present && !explicitlyNull ? emailVerified : null;
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Google ID token's email_verified claim could not be read");

            return null;
        }
    }
}
