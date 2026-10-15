using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Persistence;

namespace ArturRios.Heimdall.Command.Services;

/// <summary>
///     FR-AU-13: the per-account budget for re-authentication — every check of a password or a factor
///     made under a bearer token: enabling (UC-36), confirming (UC-37) and disabling (UC-39)
///     two-factor authentication, regenerating recovery codes (UC-40), changing one's own email
///     (UC-08) and a person requesting erasure (UC-42).
/// </summary>
/// <remarks>
///     <para>
///         <b>Why it is its own budget.</b> The caller already holds a token, and the token may be
///         stolen. Charging these checks to the login lockout (FR-AU-09) or to the second-factor
///         budget (FR-AU-12) would let a token thief lock the account's owner out of signing in, so
///         neither is touched here: a thief can only pause these checks, for the owner as for
///         themselves.
///     </para>
///     <para>
///         <b>How it is used.</b> Each check is charged before it is compared, and a check made while
///         the budget is paused is still compared, its result discarded, so a pause costs and answers
///         the same as a wrong credential. A charge that ends in success is cleared, so only failures
///         count. The refusal is always <c>AuthMessages.PasswordOrCodeIncorrect</c>.
///     </para>
/// </remarks>
public static class ReauthenticationBudget
{
    /// <summary>The failures within one <see cref="Window" /> that pause re-authentication.</summary>
    public const int MaxFailures = 5;

    /// <summary>How long a window lasts, from the first failure counted in it.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    /// <summary>How long re-authentication stays paused.</summary>
    public static readonly TimeSpan Pause = TimeSpan.FromMinutes(15);

    /// <summary>
    ///     Charges one re-authentication to <paramref name="person" />. <c>false</c> means the budget
    ///     is paused: the check may still be made, for its cost, but must not be accepted.
    /// </summary>
    public static Task<bool> TryChargeAsync(IAtomicWrites atomicWrites, Person person)
    {
        var now = DateTime.UtcNow;

        return atomicWrites.TryReserveReauthenticationAsync(person, MaxFailures, now, now - Window, now + Pause);
    }

    /// <summary>Clears the charge, and any earlier failures, once a re-authentication succeeds.</summary>
    public static Task ClearAsync(IAtomicWrites atomicWrites, Person person) =>
        atomicWrites.ClearReauthenticationAttemptsAsync(person);
}
