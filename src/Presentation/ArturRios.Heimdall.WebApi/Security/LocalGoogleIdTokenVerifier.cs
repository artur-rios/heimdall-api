using System.Text;
using ArturRios.Heimdall.Command.Services;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace ArturRios.Heimdall.WebApi.Security;

/// <summary>
///     A <see cref="IGoogleIdTokenVerifier" /> that validates ID tokens signed with a locally held
///     secret instead of by Google, so the functional suite can exercise UC-25 end-to-end without
///     reaching the network. Reads the same five claims the real verifier does.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why this exists.</b> <c>WebApiTest&lt;T&gt;</c> keeps its <c>WebApplicationFactory</c>
///         private and its <c>Gateway</c> protected-readonly, so a functional test cannot replace a
///         DI registration; every substitution in this suite is therefore chosen at start-up from the
///         environment, as <c>Startup.AddEmailSenders</c> does for Mailgun. Without a substitute only
///         AF-25a and AF-25b would be reachable — the main flow, AF-25c, and AF-25d all sit behind
///         token verification — and the Testing Specification §7.4 asks for every alternative flow.
///     </para>
///     <para>
///         <b>What it is not.</b> Not a "trust anything" stub. A token still has to carry a valid
///         HS256 signature, the expected issuer and audience, and an unexpired lifetime — the secret
///         is simply one the test fixture holds rather than one Google publishes. A test cannot forge
///         a token any more than a caller can; it can only mint one.
///     </para>
///     <para>
///         <b>What keeps it out of production.</b> Two independent guards in
///         <c>Startup.AddGoogleSignIn</c>: it is never registered in the Production environment, and
///         it is never registered unless <see cref="GoogleSignInOptions.TestSigningSecretVariable" />
///         is explicitly set. That variable is absent from every <c>.env</c> file and is set only by
///         the functional suite's <c>PostgresFixture</c>.
///     </para>
/// </remarks>
public class LocalGoogleIdTokenVerifier(
    GoogleSignInOptions options,
    ILogger<LocalGoogleIdTokenVerifier> logger) : IGoogleIdTokenVerifier
{
    /// <summary>Issuer and audience the locally minted tokens must carry. Shared with the test fixture.</summary>
    public const string TestIssuer = "heimdall-google-test";

    private static readonly JsonWebTokenHandler Handler = new();

    public async Task<GoogleIdTokenPayload?> VerifyAsync(string idToken)
    {
        if (string.IsNullOrWhiteSpace(idToken))
        {
            return null;
        }

        var result = await Handler.ValidateTokenAsync(idToken, new TokenValidationParameters
        {
            ValidIssuer = TestIssuer,
            ValidAudience = TestIssuer,
            IssuerSigningKey = new SymmetricSecurityKey(
                Encoding.UTF8.GetBytes(options.TestSigningSecret)),
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateIssuerSigningKey = true,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.Zero
        });

        if (!result.IsValid)
        {
            logger.LogDebug(result.Exception, "Locally signed Google ID token rejected");

            return null;
        }

        var subject = Claim(result, "sub");
        var email = Claim(result, "email");

        // A token missing either identifier could not name a Google User, so it fails verification
        // rather than producing a payload the handler would have to second-guess.
        if (string.IsNullOrWhiteSpace(subject) || string.IsNullOrWhiteSpace(email))
        {
            return null;
        }

        return new GoogleIdTokenPayload(
            subject,
            email,
            EmailVerified(result),
            Claim(result, "name"),
            Claim(result, "picture"));
    }

    /// <summary>
    ///     The <c>email_verified</c> claim, or <c>null</c> when the token carries none. Absence is
    ///     read straight off the validated claim set, which simply has no entry for a claim the
    ///     token never held; a value that will not parse as a boolean is treated as absent too —
    ///     a claim present but JSON <c>null</c> among them — since it asserts nothing this API can
    ///     act on, and FR-GO-19 must not downgrade a stored <c>true</c> on the strength of it.
    /// </summary>
    private static bool? EmailVerified(TokenValidationResult result) =>
        bool.TryParse(Claim(result, "email_verified"), out var verified) ? verified : null;

    private static string? Claim(TokenValidationResult result, string name) =>
        result.Claims.TryGetValue(name, out var value) ? value?.ToString() : null;
}
