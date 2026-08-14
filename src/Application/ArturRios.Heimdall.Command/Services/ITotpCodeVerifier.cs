using ArturRios.Heimdall.Domain.Entities;

namespace ArturRios.Heimdall.Command.Services;

/// <summary>
///     Checks an authenticator-app code against a person's <see cref="TwoFactorAuth" /> configuration
///     (FR-2F-04, FR-2F-09), and records the time step it accepted so the same code cannot be
///     presented twice.
/// </summary>
/// <remarks>
///     <para>
///         Extracted so UC-37's confirmation and <see cref="ITwoFactorFactorVerifier" />'s
///         "app code, or email code, or recovery code" check share one implementation. They
///         previously carried a copy each — same secret decryption, same verification window, and
///         after the first copy learned to reject a replay the second would still have accepted one.
///     </para>
///     <para>
///         Unlike <see cref="ITwoFactorFactorVerifier" />, this one writes: rejecting a replay means
///         remembering the accepted step, and the caller cannot be trusted to do that on its behalf —
///         UC-38 and UC-40 have no other reason to touch the configuration row, so a caller that
///         forgot would silently reopen the replay window with nothing failing.
///     </para>
/// </remarks>
public interface ITotpCodeVerifier
{
    /// <param name="twoFactorAuth">
    ///     The configuration holding the encrypted secret and the last accepted step. Updated in
    ///     place, and persisted, when a code is accepted.
    /// </param>
    /// <param name="code">The submitted code. A missing or malformed one is simply not a match.</param>
    /// <returns>
    ///     <see langword="true" /> when the code is currently valid and has not been used before;
    ///     <see langword="false" /> for a wrong code, an absent one, a configuration with no stored
    ///     secret, a secret that can no longer be decrypted, or a code already accepted.
    /// </returns>
    Task<bool> VerifyAsync(TwoFactorAuth twoFactorAuth, string? code);
}
