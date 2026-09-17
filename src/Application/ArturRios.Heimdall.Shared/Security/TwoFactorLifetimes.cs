namespace ArturRios.Heimdall.Shared.Security;

/// <summary>
///     The lifetimes a two-factor login attempt runs on: the emailed code's (FR-2F-03) and the
///     challenge token's (NFR-17). They are declared together, in one type, because between AF-11g
///     issuing them and UC-38 redeeming them they are not two clocks but one deadline.
/// </summary>
/// <remarks>
///     <para>
///         They were previously three separate constants — one in <c>LoginCommandHandler</c>, one in
///         <c>EnableTwoFactorAuthCommandHandler</c>, one in <c>JwtTwoFactorChallengeTokenIssuer</c> —
///         and the last of them disagreed with the other two. Both artefacts are stamped in the same
///         request, so a challenge token shorter than its code meant that for the second half of
///         every code's life the code was genuinely correct and there was nowhere left to present
///         it: FR-2F-10 makes second-factor verification the only endpoint that accepts a challenge
///         token, so once the token lapsed the code lapsed with it. Nothing was wrong with either
///         artefact; the login simply stopped being completable halfway through the window the
///         specification promised.
///     </para>
///     <para>
///         Defining <see cref="ChallengeToken" /> <em>as</em> <see cref="EmailCode" /> rather than
///         repeating the number is what keeps that from reappearing. A later change to one moves
///         both, which is the only relationship between them that is ever correct.
///     </para>
///     <para>
///         <b>Why ten minutes and not five.</b> Ten is the tolerance FR-2F-03 chose for the one part
///         of this flow outside anyone's control — how long an email takes to arrive. The App method
///         does not need it: an authenticator generates a code on demand, so its holder never races
///         delivery and would be served just as well by a much shorter window. The Email method
///         cannot do without it, and the two methods share one challenge token, so the window has to
///         be the longer of what they need. The cost is that a stolen challenge token is useful for
///         ten minutes rather than five — which is a smaller concession than it reads as, since the
///         token authorises nothing by itself: it is refused at every endpoint but second-factor
///         verification (FR-2F-10), and there it still buys nothing without a second factor.
///     </para>
///     <para>
///         Both remain fixed constants rather than configuration, for the reason NFR-17 gives: a
///         full login token's lifetime is meant to be tuned per deployment, but these two are
///         security properties of the use case itself, and an operator who could move them could
///         reintroduce the disagreement this type exists to prevent.
///     </para>
/// </remarks>
public static class TwoFactorLifetimes
{
    /// <summary>
    ///     How long an emailed 6-digit code stays redeemable after issue (FR-2F-03).
    /// </summary>
    public static readonly TimeSpan EmailCode = TimeSpan.FromMinutes(10);

    /// <summary>
    ///     How long the challenge token issued alongside that code stays usable (NFR-17). Equal to
    ///     <see cref="EmailCode" /> by definition — see the remarks.
    /// </summary>
    public static readonly TimeSpan ChallengeToken = EmailCode;
}
