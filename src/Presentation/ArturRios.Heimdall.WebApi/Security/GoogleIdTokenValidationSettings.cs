using Google.Apis.Auth;

namespace ArturRios.Heimdall.WebApi.Security;

/// <summary>
///     Builds the <see cref="GoogleJsonWebSignature.ValidationSettings" /> UC-25's verification runs
///     under (NFR-13).
/// </summary>
/// <remarks>
///     <para>
///         Split out of <see cref="GoogleIdTokenVerifier" /> for the reason
///         <see cref="GoogleIdTokenClaims" /> was: the verifier itself calls the static,
///         network-bound
///         <see cref="GoogleJsonWebSignature.ValidateAsync(string, GoogleJsonWebSignature.ValidationSettings)" />,
///         which offers no seam, and reaching it in a test would mean contacting Google or holding
///         Google's signing key. What is left here is a pure function of the configured options, so
///         <c>GoogleIdTokenValidationSettingsTests</c> can pin it.
///     </para>
///     <para>
///         Which matters more than it looks. Of the four checks NFR-13 names — signature, issuer,
///         audience, expiry — three are Google's library's to make and are not ours to get wrong.
///         The audience is the one this application supplies, and it is the one that decides whether
///         a token minted for somebody else's application is accepted. Google's own contract is that
///         a <c>null</c> <see cref="GoogleJsonWebSignature.ValidationSettings.Audience" /> means "do
///         not validate the audience at all", so the difference between enforcing that check and
///         silently dropping it is one assignment in this file — a change that no test could see
///         while the settings were built inline.
///     </para>
/// </remarks>
public static class GoogleIdTokenValidationSettings
{
    /// <summary>
    ///     The settings for a deployment configured with <paramref name="options" />' client IDs.
    /// </summary>
    /// <remarks>
    ///     Only the audience is set. The issuer (<c>accounts.google.com</c>), the signature against
    ///     Google's published keys, and the expiry are the library's defaults, and overriding any of
    ///     them here would be loosening a check NFR-13 requires rather than configuring one.
    /// </remarks>
    public static GoogleJsonWebSignature.ValidationSettings For(GoogleSignInOptions options) => new()
    {
        // Never null, and never empty in practice: Startup resolves UnconfiguredGoogleIdTokenVerifier
        // when no client is configured, so this verifier is only ever reached with at least one
        // audience to check against. Passing the list straight through is what makes a token issued
        // for another application fail even though Google signed it.
        Audience = options.ClientIds
    };
}
