using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Persistence;
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
    /// <param name="atomicWrites">Charges each guess, and retires a code at the cap.</param>
    /// <param name="twoFactorAuthId">The configuration whose codes are considered.</param>
    /// <param name="code">The submitted code.</param>
    /// <returns>
    ///     The matching code, or <c>null</c> when none matches. A missing, incorrect, expired,
    ///     already-used, or exhausted code all answer alike — UC-37 and UC-38 distinguish none of
    ///     them, and a caller who could tell "wrong" from "no attempts left" would learn how much
    ///     budget remained. The caller spends the matching code itself, once its own checks pass.
    /// </returns>
    public static async Task<TwoFactorEmailCode?> FindMatchingAsync(
        IAsyncReadOnlyRepository<TwoFactorEmailCode, long> emailCodeReader,
        IAtomicWrites atomicWrites,
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

        // A guess is charged to every code currently outstanding, not to one of them: there is no
        // single code it is "aimed at", and charging nothing would leave the budget unspent. In
        // practice a configuration holds one live code at a time — both issuing paths retire the
        // previous one first. A code whose budget is already spent, or that a parallel request has
        // just spent, is not compared at all.
        var charged = new List<TwoFactorEmailCode>(live.Count);

        foreach (var outstanding in live)
        {
            if (await atomicWrites.TryChargeEmailCodeGuessAsync(outstanding, MaxFailedAttempts, now))
            {
                charged.Add(outstanding);
            }
        }

        // Through the gate, like every other Argon2id derivation on a request path (TH-03): the code
        // is hashed with the same 600 MB parameters as a password, so a comparison outside the bound
        // was memory the bound did not know about.
        foreach (var candidate in charged)
        {
            if (await PasswordHashGate.Shared.TextMatchesAsync(code, candidate.CodeHash, candidate.Salt))
            {
                return candidate;
            }
        }

        // A miss: any code it brought to the cap is retired. Bookkeeping either way — the guess was
        // wrong, and neither use case defines a flow in which a caller is told it could not be kept.
        foreach (var exhausted in charged)
        {
            await atomicWrites.RetireEmailCodeIfExhaustedAsync(exhausted, MaxFailedAttempts);
        }

        return null;
    }
}
