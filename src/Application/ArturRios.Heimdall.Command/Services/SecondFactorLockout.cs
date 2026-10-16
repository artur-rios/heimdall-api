namespace ArturRios.Heimdall.Command.Services;

/// <summary>
///     FR-AU-12: the per-account budget for second factors presented at sign-in (UC-38) — an
///     authenticator-app code, an email code or a recovery code alike.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why per account, and not per challenge.</b> The challenge's own budgets (FR-2F-13,
///         FR-2F-17) start afresh with every login, and a login costs whoever knows the password
///         nothing — which is the one caller a second factor exists to stop. So the failures are
///         counted on the account, across every challenge, and a correct password does not reset
///         them: only a second factor that checks out does.
///     </para>
///     <para>
///         <b>Why the lock escalates.</b> A fixed lock caps guessing at a steady rate, and at ten
///         guesses every fifteen minutes the third of a million guesses a six-digit code takes on
///         average still fit in a year. Doubling each lock, up to a day, brings a persistent attacker
///         down to ten guesses a day, while a person who mistypes ten times waits fifteen minutes, as
///         they would at the password. The count of locks resets when a second factor succeeds.
///     </para>
///     <para>
///         <b>Why the lock does not show.</b> A locked account answers UC-38's ordinary refusal, and
///         the factor is still verified — the result discarded — so neither the message nor the cost
///         of the answer tells the caller that guessing has stopped counting.
///     </para>
/// </remarks>
public static class SecondFactorLockout
{
    /// <summary>The failures that lock the second factor.</summary>
    public const int MaxFailedAttempts = 10;

    /// <summary>How long the first lock lasts. Each one after it lasts twice as long as the last.</summary>
    public static readonly TimeSpan FirstLockout = TimeSpan.FromMinutes(15);

    /// <summary>The longest any lock lasts.</summary>
    public static readonly TimeSpan LongestLockout = TimeSpan.FromHours(24);

    /// <summary>
    ///     When each lock that started at <paramref name="now" /> would end, by how many locks came
    ///     before it: the first, the second, and so on, the last entry standing for every lock from the
    ///     cap on. The shape <c>IAtomicWrites.TryReserveSecondFactorAttemptAsync</c> takes.
    /// </summary>
    public static IReadOnlyList<DateTime> LockoutEndsFrom(DateTime now)
    {
        var ends = new List<DateTime>();

        for (var lockout = FirstLockout; ; lockout *= 2)
        {
            if (lockout >= LongestLockout)
            {
                ends.Add(now + LongestLockout);

                return ends;
            }

            ends.Add(now + lockout);
        }
    }
}
