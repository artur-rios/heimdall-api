namespace ArturRios.Heimdall.Command.Services;

/// <summary>
///     The person a UC-38 challenge token was redeemed for. Carries only what
///     <c>VerifyTwoFactorAuthCommandHandler</c> needs to look the person back up — the rest of the
///     token's claims are the presentation layer's concern.
/// </summary>
/// <param name="PersonId">The <c>PublicId</c> of the person the challenge token names.</param>
public record TwoFactorChallengePrincipal(Guid PersonId);

/// <summary>
///     Issues the short-lived challenge token AF-11g returns instead of a full authentication token
///     (FR-2F-07, NFR-17), when a person with active two-factor authentication passes UC-11's
///     password check. Separate from <see cref="IAuthTokenIssuer" /> because the two tokens carry
///     deliberately different claims — this one is scoped only to second-factor verification and
///     expires far sooner.
/// </summary>
public interface ITwoFactorChallengeTokenIssuer
{
    /// <param name="personId">The <c>PublicId</c> of the person who passed the password check.</param>
    /// <param name="roleId">Their role value (see <c>Roles</c>) — carried so the mapper that reads the token back can build an identity from it, but never authorizes anything by itself while <c>MfaPending</c> is set.</param>
    /// <returns>The signed challenge token and its (short) expiry.</returns>
    Task<AuthToken> IssueAsync(Guid personId, int roleId);
}

/// <summary>
///     Validates a UC-38 challenge token — signature, expiry, and the MFA-pending claim (AF-38a,
///     FR-2F-10) — and resolves the person it names, without any database read.
/// </summary>
public interface ITwoFactorChallengeTokenValidator
{
    /// <param name="token">The challenge token submitted to <c>POST /api/auth/2fa/verify</c>.</param>
    /// <returns>The named person, or <see langword="null" /> if the token is missing, malformed, unsigned by this API, expired, or does not carry the MFA-pending claim.</returns>
    Task<TwoFactorChallengePrincipal?> ValidateAsync(string? token);
}
