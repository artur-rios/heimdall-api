using System.Text;
using ArturRios.Jwt;

namespace ArturRios.Heimdall.WebApi.Security;

/// <summary>
///     The signing secret, and the previous one during a rotation (Threat Model TH-22), read once at
///     start-up from <c>HEIMDALL_AUTH_TOKEN_SECRET</c> and <c>HEIMDALL_AUTH_TOKEN_SECRET_PREVIOUS</c>
///     and turned into the keys tokens are signed and validated with.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why a secret is refused rather than used.</b> HMAC-SHA256 needs a key at least as long
///         as its 256-bit output, so a secret shorter than <see cref="MinimumBytes" /> bytes is
///         refused. <c>ArturRios.Jwt</c> reads a secret's ASCII bytes, and ASCII replaces every
///         character above U+007F with <c>?</c>, so a secret holding any such character would sign
///         with far less entropy than it appears to carry, and two different secrets could become
///         the same key. Such a secret is refused too. Either refusal stops start-up, naming the
///         variable and never its value, as every other unusable security setting does.
///     </para>
///     <para>
///         <b>An empty previous secret is no previous secret.</b> Unset, blank, or two double quotes
///         (<c>""</c>, what an env file's empty quoted value becomes when nothing strips the quotes)
///         all mean "no rotation in progress", so nothing is passed to the library as a key. A
///         previous secret equal to the current one is ignored as well: the same value under two ids
///         says nothing has been rotated yet. Any other previous secret is held to the same rules as
///         the current one.
///     </para>
///     <para>
///         The keys carry ids because <see cref="JwtKey" /> requires them, but no <c>kid</c> is
///         written to the tokens; see <c>Startup.BuildJwtConfiguration</c> for why.
///     </para>
/// </remarks>
public static class TokenSigningSecrets
{
    /// <summary>The variable carrying the secret new tokens are signed with. Required.</summary>
    public const string SecretVariable = "HEIMDALL_AUTH_TOKEN_SECRET";

    /// <summary>The variable carrying the secret a rotation has moved off but not yet withdrawn. Optional.</summary>
    public const string PreviousSecretVariable = "HEIMDALL_AUTH_TOKEN_SECRET_PREVIOUS";

    /// <summary>The shortest secret accepted, in bytes: the 256-bit output of HMAC-SHA256.</summary>
    public const int MinimumBytes = 32;

    /// <summary>The id of the key new tokens are signed with.</summary>
    public const string CurrentKeyId = "current";

    /// <summary>The id of the key a rotation still accepts.</summary>
    public const string PreviousKeyId = "previous";

    /// <summary>
    ///     A previous secret's value that means "none": two double quotes, left behind when an env
    ///     file's empty quoted value reaches the process with its quotes intact.
    /// </summary>
    private const string EmptyQuotedValue = "\"\"";

    /// <summary>
    ///     Builds the keys from raw values: the current secret first, then the previous one when a
    ///     rotation is in progress.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    ///     <paramref name="secret" /> is blank, either secret is shorter than <see cref="MinimumBytes" />
    ///     bytes, or either holds a character outside ASCII. The message names the variable and never
    ///     the value.
    /// </exception>
    public static IReadOnlyList<JwtKey> Parse(string? secret, string? previousSecret)
    {
        if (string.IsNullOrWhiteSpace(secret))
        {
            throw new InvalidOperationException(
                $"Environment variable '{SecretVariable}' is unset. The API cannot validate tokens " +
                "without a signing secret.");
        }

        EnsureUsable(SecretVariable, secret);

        List<JwtKey> keys = [new(CurrentKeyId, secret)];

        if (IsSet(previousSecret) && previousSecret != secret)
        {
            EnsureUsable(PreviousSecretVariable, previousSecret!);
            keys.Add(new JwtKey(PreviousKeyId, previousSecret!));
        }

        return keys;
    }

    /// <summary>Whether a previous secret is configured at all: neither blank nor an empty quoted value.</summary>
    private static bool IsSet(string? previousSecret) =>
        !string.IsNullOrWhiteSpace(previousSecret) &&
        !string.Equals(previousSecret.Trim(), EmptyQuotedValue, StringComparison.Ordinal);

    private static void EnsureUsable(string variable, string value)
    {
        // Checked first: for a secret outside ASCII the byte count below would describe bytes the
        // library never signs with.
        if (value.Any(character => character > '\u007F'))
        {
            throw new InvalidOperationException(
                $"Environment variable '{variable}' holds characters outside ASCII. Tokens are signed " +
                "with the secret's ASCII bytes, which turn each such character into '?', so the key " +
                "would be weaker than it looks. Use a random ASCII secret, e.g. 'openssl rand -base64 48'. " +
                "Refusing to start.");
        }

        var bytes = Encoding.UTF8.GetByteCount(value);

        if (bytes < MinimumBytes)
        {
            throw new InvalidOperationException(
                $"Environment variable '{variable}' is {bytes} bytes. A signing secret must be at least " +
                $"{MinimumBytes} bytes, which HMAC-SHA256 requires; generate it randomly, e.g. " +
                "'openssl rand -base64 48'. Refusing to start.");
        }
    }
}
