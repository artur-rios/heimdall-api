using ArturRios.Heimdall.Domain.Entities;

namespace ArturRios.Heimdall.Command.Services;

/// <summary>
///     Issues the email two-factor code (FR-2F-03, FR-2F-08): retires whatever the configuration
///     still has outstanding, then generates, persists and mails a fresh 6-digit one.
/// </summary>
/// <remarks>
///     <para>
///         Three use cases need exactly this step and had a copy each — UC-36 when the Email method
///         is selected, UC-11's AF-11g when a challenge is issued, and UC-46 when the code is asked
///         for again. The copies did the same thing, which is the problem: the retirement is what
///         makes a code single-use in practice (only the newest one can ever be redeemed), so a
///         fourth caller that issued without retiring, or a copy that drifted, would quietly leave
///         two live codes for one configuration.
///     </para>
///     <para>
///         An injected service rather than a static helper like
///         <see cref="TwoFactorEmailCodeVerification" />: this one needs a mail sender as well as the
///         two repositories, and a sender is a dependency the composition root chooses (it differs
///         between environments), not a parameter every call site should have to thread through.
///     </para>
/// </remarks>
public interface ITwoFactorEmailCodeIssuer
{
    /// <summary>
    ///     Retires the configuration's outstanding codes and issues a fresh one, mailed to
    ///     <paramref name="email" />.
    /// </summary>
    /// <param name="twoFactorAuth">The configuration the code belongs to.</param>
    /// <param name="email">The address to mail it to — the person's own, never one a caller supplied.</param>
    /// <returns>
    ///     <see langword="null" /> on success, or the persistence errors that stopped it. Delivery
    ///     failures are not reported: the code is persisted before it is sent, and no use case
    ///     defines a flow in which a caller is told the mail did not go out — that is precisely what
    ///     UC-46 exists to let them retry.
    /// </returns>
    Task<IEnumerable<string>?> ReissueAsync(TwoFactorAuth twoFactorAuth, string email);

    /// <summary>
    ///     Marks every not-yet-used, not-yet-expired code for the configuration as used, without
    ///     issuing a replacement — what UC-36 does when the Email method is deselected and the
    ///     outstanding code should simply stop working.
    /// </summary>
    /// <returns><see langword="null" /> on success, or the persistence errors that stopped it.</returns>
    Task<IEnumerable<string>?> RetireOutstandingAsync(long twoFactorAuthId);
}
