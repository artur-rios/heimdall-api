using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Util.Hashing;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Heimdall.Command.Services;

/// <summary>
///     Matches a submitted email two-factor code against the live codes issued for a configuration,
///     and counts the guesses that miss so a code cannot be brute-forced (FR-2F-13).
/// </summary>
/// <remarks>
///     <para>
///         Shared by UC-37's confirmation and <see cref="ITwoFactorFactorVerifier" />, which had a
///         copy each. A static helper rather than another injected service because it holds no state
///         and no configuration — it is the comparison itself, and giving it an interface would only
///         add a registration for callers that always want exactly this behaviour.
///     </para>
///     <para>
///         <b>Why the counter.</b> The code is six digits, so a million values, and lives ten
///         minutes. The per-IP request limiter bounds one source's rate but not an attacker
///         distributed across many, which is cheap. Retiring a code after
///         <see cref="MaxFailedAttempts" /> misses caps what any single issued code can ever be worth
///         and makes further guessing cost a fresh login — which is itself limited, and which mails
///         the account holder a code they did not ask for.
///     </para>
/// </remarks>
public static class TwoFactorEmailCodeVerification
{
    /// <summary>Wrong guesses a single issued code tolerates before it is retired.</summary>
    public const int MaxFailedAttempts = 5;

    /// <param name="emailCodeReader">Reads the configuration's outstanding codes.</param>
    /// <param name="emailCodeWriter">Persists the attempt count, and the retirement at the cap.</param>
    /// <param name="twoFactorAuthId">The configuration whose codes are considered.</param>
    /// <param name="code">The submitted code.</param>
    /// <returns>
    ///     The matching code, or <c>null</c> when none matches. A missing, incorrect, expired,
    ///     already-used, or exhausted code all answer alike — UC-37 and UC-38 distinguish none of
    ///     them, and a caller who could tell "wrong" from "no attempts left" would learn how much
    ///     budget remained.
    /// </returns>
    public static async Task<TwoFactorEmailCode?> FindMatchingAsync(
        IAsyncReadOnlyRepository<TwoFactorEmailCode> emailCodeReader,
        IAsyncRepository<TwoFactorEmailCode> emailCodeWriter,
        long twoFactorAuthId,
        string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        var now = DateTime.UtcNow;

        var live = await emailCodeReader.Query()
            .Where(x => x.TwoFactorAuthId == twoFactorAuthId && !x.Used && x.ExpiresAt > now)
            .ToListAsync();

        var match = live.FirstOrDefault(x => Hash.TextMatches(code, x.CodeHash, x.Salt));

        if (match is not null)
        {
            return match;
        }

        // A miss is charged to every code currently outstanding, not to one of them: the submitted
        // value matched none, so there is no single code it was "aimed at", and charging nothing
        // would leave the budget unspent. In practice a configuration holds one live code at a time —
        // both issuing paths retire the previous one first.
        foreach (var outstanding in live)
        {
            outstanding.FailedAttempts++;

            if (outstanding.FailedAttempts >= MaxFailedAttempts)
            {
                outstanding.Used = true;
            }

            // A failure to persist the count is swallowed: the guess was wrong either way, and
            // neither use case defines a flow in which a caller is told the bookkeeping failed.
            await emailCodeWriter.UpdateAsync(outstanding);
        }

        return null;
    }
}
