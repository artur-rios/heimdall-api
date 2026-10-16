using ArturRios.Jwt;

namespace ArturRios.Heimdall.WebApi.Security;

/// <summary>
///     The audiences of the three tokens Heimdall issues to a person: the full token's, configured as
///     <c>HEIMDALL_AUTH_TOKEN_AUDIENCE</c>, and the two derived from it for the tokens that are not a
///     sign-in — the UC-38 challenge token (NFR-17) and the subject-rights-only token (NFR-24).
/// </summary>
/// <remarks>
///     <para>
///         <b>Why the audiences differ.</b> All three are signed with the same keys under the same
///         issuer. What makes a challenge token or a subject-rights-only token less than a sign-in is a
///         claim only Heimdall reads (<c>mfaPending</c>, <c>subjectRightsOnly</c>). A consumer that
///         validates the signature, issuer, audience and lifetime would take either for a full token
///         if they shared its audience: a password alone would get past the second factor there, and a
///         restricted identity would act as an unrestricted one (Threat Model TH-06, TH-35). A distinct
///         audience makes a consumer that pins the person audience refuse both, by construction and
///         with no knowledge of Heimdall's claims, as it refuses application tokens.
///     </para>
///     <para>
///         <b>Why derived rather than configured.</b> Unlike <see cref="ApplicationTokenOptions" />, no
///         variable is read: a suffix on the person audience is distinct from it and from the other
///         suffix whatever the person audience is, so the three person audiences can never be set
///         equal, and there is nothing new to set. The application audience is configured, though,
///         and could be; <see cref="ApplicationTokenOptions.Parse" /> refuses it equal to either
///         derived audience at start-up. The suffixes are fixed so a consumer can name the audiences
///         too.
///     </para>
///     <para>
///         <b>Inside Heimdall.</b> <see cref="PersonTokenValidator" /> admits a bearer token only
///         under the configured issuer and one of these three audiences (<see cref="TokenOrigin" />),
///         and <see cref="IdentityUserMapper" /> holds each audience to its claim: a token is mapped
///         only when its audience and its <c>mfaPending</c> and <c>subjectRightsOnly</c> claims agree.
///     </para>
/// </remarks>
/// <param name="Person">
///     The audience of a full token, <c>HEIMDALL_AUTH_TOKEN_AUDIENCE</c> exactly as configured.
/// </param>
public sealed record PersonTokenAudiences(string Person)
{
    /// <summary>The suffix that turns the person audience into the challenge token's.</summary>
    public const string TwoFactorChallengeSuffix = ".2fa-challenge";

    /// <summary>The suffix that turns the person audience into the subject-rights-only token's.</summary>
    public const string SubjectRightsSuffix = ".subject-rights";

    /// <summary>The audience of a UC-38 challenge token: the person audience plus <see cref="TwoFactorChallengeSuffix" />.</summary>
    public string TwoFactorChallenge => Person + TwoFactorChallengeSuffix;

    /// <summary>The audience of a subject-rights-only token: the person audience plus <see cref="SubjectRightsSuffix" />.</summary>
    public string SubjectRights => Person + SubjectRightsSuffix;

    /// <summary>The audiences derived from the configuration full tokens are issued and validated with.</summary>
    public static PersonTokenAudiences From(JwtConfiguration configuration) => new(configuration.Audience);
}
