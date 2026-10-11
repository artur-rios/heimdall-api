using ArturRios.Mediator.Command;

namespace ArturRios.Heimdall.Command.Output;

/// <summary>
///     Result of <see cref="Input.LoginCommand" /> (UC-11): either the issued authentication token
///     and when it expires (FR-AU-03), or — AF-11g, when the person has active two-factor
///     authentication — a short-lived challenge token and the second-factor methods available,
///     leaving <see cref="Token" />/<see cref="ExpiresAt" /> unset. <see cref="RequiresTwoFactor" />
///     tells a caller which shape they got. The full token's own claims carry the person's and
///     scopes' <c>PublicId</c>s (FR-AU-04); the only thing about the person repeated here is
///     <see cref="EmailVerified" />, which no claim carries.
/// </summary>
public class LoginCommandOutput : CommandOutput
{
    /// <summary>The signed authentication token, to be sent as a bearer token. Null when <see cref="RequiresTwoFactor" /> is true.</summary>
    public string? Token { get; set; }

    /// <summary>When the token expires, in UTC. Null when <see cref="RequiresTwoFactor" /> is true.</summary>
    public DateTime? ExpiresAt { get; set; }

    /// <summary>
    ///     Whether the authenticated person's email address is verified (FR-EV-05), so a caller
    ///     knows whether to prompt them and offer <c>POST /api/auth/resend-verification</c> (UC-15).
    ///     Null when <see cref="RequiresTwoFactor" /> is true: the caller has passed only the first
    ///     factor and is not authenticated yet, so this response tells them nothing about the
    ///     account. They receive it from <c>POST /api/auth/2fa/verify</c> instead (UC-38).
    /// </summary>
    public bool? EmailVerified { get; set; }

    /// <summary>
    ///     AF-11g (FR-2F-07): true when the person has active two-factor authentication, so this
    ///     response carries a challenge token instead of a full one — see UC-38.
    /// </summary>
    public bool RequiresTwoFactor { get; set; }

    /// <summary>
    ///     The short-lived UC-38 challenge token (NFR-17), present only when
    ///     <see cref="RequiresTwoFactor" /> is true. Submitted to <c>POST /api/auth/2fa/verify</c> as
    ///     a request body field — never as a bearer credential (FR-2F-10).
    /// </summary>
    public string? ChallengeToken { get; set; }

    /// <summary>
    ///     The second-factor methods available to the person — <c>["App"]</c>, <c>["Email"]</c>, or
    ///     both — present only when <see cref="RequiresTwoFactor" /> is true.
    /// </summary>
    public IReadOnlyCollection<string>? AvailableMethods { get; set; }

    /// <summary>
    ///     True when processing of the identity is restricted (NFR-24), so <see cref="Token" /> is a
    ///     subject-rights-only token: it is accepted only where the subject exports, erases, restricts,
    ///     or lifts their own restriction (UC-41, UC-42, UC-44, UC-45), and refused everywhere else.
    ///     A client should offer those actions — lifting the restriction above all — and ask the
    ///     subject to sign in again once it is lifted, for a full token.
    /// </summary>
    public bool ProcessingRestricted { get; set; }
}
