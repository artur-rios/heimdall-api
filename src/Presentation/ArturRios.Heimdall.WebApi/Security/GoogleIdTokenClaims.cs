using Google.Apis.Auth;
// Google.Apis.Auth has a JsonWebToken of its own; the alias names the one meant without hiding it.
using JsonWebToken = Microsoft.IdentityModel.JsonWebTokens.JsonWebToken;

namespace ArturRios.Heimdall.WebApi.Security;

/// <summary>
///     Reads claims off a Google ID token that has <em>already</em> been validated, for the questions
///     <see cref="GoogleJsonWebSignature.Payload" /> cannot answer.
/// </summary>
/// <remarks>
///     Split out of <see cref="GoogleIdTokenVerifier" /> so it can be tested directly: the verifier
///     itself calls the static, network-bound
///     <see cref="GoogleJsonWebSignature.ValidateAsync(string, GoogleJsonWebSignature.ValidationSettings)" />,
///     which offers no seam, and reaching it in a test would mean contacting Google or forging
///     Google's signature. Everything here is a pure function of the token string, so
///     <c>GoogleIdTokenClaimsTests</c> can pin the behaviour the verifier depends on without either.
/// </remarks>
public static class GoogleIdTokenClaims
{
    private const string EmailVerifiedClaim = "email_verified";

    /// <summary>
    ///     The <c>email_verified</c> claim, or <c>null</c> when the token asserts nothing — the claim
    ///     is absent, is JSON <c>null</c>, or holds something that is not a boolean.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <see cref="GoogleJsonWebSignature.Payload.EmailVerified" /> is a non-nullable
    ///         <c>bool</c>, so a token that omitted the claim deserializes to the same <c>false</c> a
    ///         token asserting "not verified" does. The distinction matters to FR-GO-19, which must
    ///         leave a stored <c>true</c> alone rather than downgrade it, so the claim is read off the
    ///         token itself with <see cref="JsonWebToken.TryGetPayloadValue{T}(string, out T)" />,
    ///         which answers presence and value in one call and leaves segment splitting and
    ///         base64url decoding to the library.
    ///     </para>
    ///     <para>
    ///         The second probe covers the one case the first gets wrong. Measured against
    ///         Microsoft.IdentityModel.JsonWebTokens 8.19.2, <c>TryGetPayloadValue&lt;bool&gt;</c>
    ///         reports an explicit <c>"email_verified": null</c> as present-and-<c>false</c>, which
    ///         would be exactly the silent downgrade FR-GO-19 forbids; the <c>bool?</c> overload
    ///         succeeds only for that null-valued claim — an absent claim and a real <c>true</c> or
    ///         <c>false</c> all make it fail — so it isolates it precisely. That discriminator is
    ///         measured behaviour rather than a documented contract, which is exactly why
    ///         <c>GoogleIdTokenClaimsTests</c> pins all four shapes: a package bump that changes it
    ///         fails the suite instead of quietly downgrading someone's verified address.
    ///     </para>
    ///     <para>
    ///         Anything thrown becomes "absent" rather than escaping. The caller's catch handles only
    ///         <see cref="InvalidJwtException" />, so an exception from here would leave a token
    ///         Google itself vouched for failing as a 500 instead of signing the caller in. "Absent"
    ///         is the safe answer: it writes nothing.
    ///     </para>
    /// </remarks>
    /// <param name="idToken">The token, already validated by the caller.</param>
    /// <param name="logger">Optional; records why a token's claim could not be read, at debug level.</param>
    public static bool? EmailVerified(string idToken, ILogger? logger = null)
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
            logger?.LogDebug(exception, "Google ID token's email_verified claim could not be read");

            return null;
        }
    }
}
