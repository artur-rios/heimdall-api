using System.Security.Cryptography;
using System.Text;

namespace ArturRios.Heimdall.WebApi.Security;

/// <summary>
///     The keys a client system may present to have its end users' own addresses counted by the
///     anonymous-auth rate limiter, read once at start-up from <c>HEIMDALL_TRUSTED_CALLER_KEYS</c>: a
///     comma-separated list of secrets.
/// </summary>
/// <remarks>
///     <para>
///         A client system such as Fortuna's API reaches Heimdall from one address for every one of
///         its users, so the connection address — even the real one, behind
///         <see cref="TrustedProxyOptions" /> — puts them all in the same rate-limit partition, and
///         ten sign-in attempts a minute from anyone lock out everybody. The client system knows
///         who its caller is; this lets it say so. A request carrying a key listed here, in
///         <c>X-Heimdall-Caller-Key</c>, is counted under the address in
///         <c>X-Heimdall-Client-Address</c> instead (see <see cref="AuthRateLimitPartition" />).
///     </para>
///     <para>
///         That is all a key grants. It authenticates nobody and authorizes nothing: a wrong or
///         missing key, or an unusable address, falls back silently to the connection address. A
///         leaked key lets its holder choose their own bucket, which only spreads guesses over
///         invented addresses; the per-account lockouts still count them (Threat Model TB-1).
///     </para>
///     <para>
///         Several keys may be listed so one can be rotated in before the other is withdrawn. Keys
///         are secrets, so one shorter than <see cref="MinimumKeyLength" /> is refused at start-up
///         rather than trusted — and the refusal names its position, never its value. Unset, no
///         caller is trusted.
///     </para>
/// </remarks>
public sealed class TrustedCallerOptions
{
    /// <summary>Comma-separated secrets a client system may present.</summary>
    public const string KeysVariable = "HEIMDALL_TRUSTED_CALLER_KEYS";

    /// <summary>The shortest key accepted, in characters.</summary>
    public const int MinimumKeyLength = 32;

    private byte[][] KeyBytes { get; init; } = [];

    /// <summary>The configured keys, trimmed. Never log these.</summary>
    public IReadOnlyList<string> Keys { get; private init; } = [];

    /// <summary>Whether any caller is trusted.</summary>
    public bool Configured => Keys.Count > 0;

    public static TrustedCallerOptions FromEnvironment() => Parse(Environment.GetEnvironmentVariable(KeysVariable));

    /// <summary>
    ///     Interprets a <see cref="KeysVariable" /> value. Split from <see cref="FromEnvironment" /> so
    ///     it can be tested without mutating the process environment the functional suite's hosts read.
    /// </summary>
    /// <exception cref="InvalidOperationException">An entry is shorter than <see cref="MinimumKeyLength" />.</exception>
    public static TrustedCallerOptions Parse(string? value)
    {
        var entries = (value ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        for (var index = 0; index < entries.Length; index++)
        {
            if (entries[index].Length < MinimumKeyLength)
            {
                throw new InvalidOperationException(
                    $"Entry {index + 1} of {KeysVariable} is {entries[index].Length} characters. Caller keys are " +
                    $"secrets and must be at least {MinimumKeyLength} characters; generate them randomly. " +
                    "Refusing to start.");
            }
        }

        return new TrustedCallerOptions
        {
            Keys = entries,
            KeyBytes = entries.Select(Encoding.UTF8.GetBytes).ToArray()
        };
    }

    /// <summary>
    ///     Whether <paramref name="presented" /> is one of the configured keys. Every configured key is
    ///     compared, with a constant-time comparison, whatever the earlier ones gave, so how long the
    ///     answer takes says nothing about which key — or how much of one — was right.
    /// </summary>
    public bool Matches(string? presented)
    {
        if (!Configured || string.IsNullOrEmpty(presented))
        {
            return false;
        }

        var presentedBytes = Encoding.UTF8.GetBytes(presented);
        var matched = false;

        foreach (var key in KeyBytes)
        {
            // Bitwise, not short-circuiting: the loop must visit every key.
            matched |= CryptographicOperations.FixedTimeEquals(key, presentedBytes);
        }

        return matched;
    }
}
