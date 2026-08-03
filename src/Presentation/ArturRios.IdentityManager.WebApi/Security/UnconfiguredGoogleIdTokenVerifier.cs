using ArturRios.IdentityManager.Command.Services;

namespace ArturRios.IdentityManager.WebApi.Security;

/// <summary>
///     The <see cref="IGoogleIdTokenVerifier" /> a deployment with no Google client configured runs
///     with: it refuses every token, so UC-25 answers <c>401</c> (AF-25a) rather than accepting one
///     no one checked.
/// </summary>
/// <remarks>
///     The refusing default is the point. Verification needs an audience to check against (NFR-13),
///     and a verifier with no configured client ID could only either reject everything or trust
///     everything — so it rejects. This differs from the email fallback, which degrades to logging
///     because a missing verification mail is an inconvenience; a missing token check is an open
///     door. Failing start-up instead was the alternative, and it would take the whole API down over
///     one optional feature that a scope must also switch on (FR-GO-01) before it is reachable.
/// </remarks>
public class UnconfiguredGoogleIdTokenVerifier(ILogger<UnconfiguredGoogleIdTokenVerifier> logger)
    : IGoogleIdTokenVerifier
{
    public Task<GoogleIdTokenPayload?> VerifyAsync(string idToken)
    {
        logger.LogWarning(
            "Google sign-in attempted but no Google client is configured ({Variable}); the token " +
            "cannot be verified and is refused",
            GoogleSignInOptions.ClientIdsVariable);

        return Task.FromResult<GoogleIdTokenPayload?>(null);
    }
}
