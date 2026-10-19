using System.Diagnostics.CodeAnalysis;
using System.Text;
using ArturRios.Jwt;

namespace ArturRios.Heimdall.WebApi.Security;

/// <summary>
///     The legacy HS256 secret, and the previous one during a rotation (Threat Model TH-22), read once
///     at start-up from <c>HEIMDALL_AUTH_TOKEN_SECRET</c> and <c>HEIMDALL_AUTH_TOKEN_SECRET_PREVIOUS</c>
///     and turned into the keys <see cref="LegacyTokenValidator" /> verifies HS256 tokens with.
/// </summary>
/// <remarks>
///     <para>
///         <b>Legacy, and optional.</b> Heimdall signs every token ES256 (<see cref="TokenSigningKeys" />)
///         and never signs with these secrets. They are read only so that, during the overlap after
///         the switch, the HS256 tokens Heimdall issued earlier stay valid until they expire (spec
///         decision 5). An unset secret means the overlap is over: no key is made, and every HS256
///         token is refused. A previous secret set without the secret is a half-removed
///         configuration, so it stops start-up rather than being ignored.
///     </para>
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
///         <b>An empty secret is no secret.</b> Unset, blank, or two double quotes (<c>""</c>, what an
///         env file's empty quoted value becomes when nothing strips the quotes) all mean "not
///         configured", for either variable. An empty previous secret means no rotation is in
///         progress, so nothing is passed to the library as a key. A previous secret equal to the
///         current one is ignored as well: the same value under two ids says nothing has been rotated
///         yet. Any other previous secret is held to the same rules as the current one.
///     </para>
///     <para>
///         The keys carry ids because <see cref="JwtKey" /> requires them, but Heimdall never wrote a
///         <c>kid</c> to an HS256 token, so <see cref="LegacyTokenValidator" /> refuses any HS256
///         token that carries one.
///     </para>
/// </remarks>
public static class TokenSigningSecrets
{
    /// <summary>The variable carrying the legacy secret HS256 tokens were signed with. Optional.</summary>
    public const string SecretVariable = "HEIMDALL_AUTH_TOKEN_SECRET";

    /// <summary>The variable carrying the secret a rotation has moved off but not yet withdrawn. Optional.</summary>
    public const string PreviousSecretVariable = "HEIMDALL_AUTH_TOKEN_SECRET_PREVIOUS";

    /// <summary>The shortest secret accepted, in bytes: the 256-bit output of HMAC-SHA256.</summary>
    public const int MinimumBytes = 32;

    /// <summary>The id of the key made of the legacy secret.</summary>
    public const string CurrentKeyId = "current";

    /// <summary>The id of the key made of the previous legacy secret.</summary>
    public const string PreviousKeyId = "previous";

    /// <summary>
    ///     A secret's value that means "none": two double quotes, left behind when an env file's empty
    ///     quoted value reaches the process with its quotes intact.
    /// </summary>
    private const string EmptyQuotedValue = "\"\"";

    /// <summary>
    ///     Builds the keys from raw values: the current secret first, then the previous one when a
    ///     rotation is in progress; none when the secret is not set.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    ///     <paramref name="previousSecret" /> is set without <paramref name="secret" />, either secret is
    ///     shorter than <see cref="MinimumBytes" /> bytes, or either holds a character outside ASCII. The
    ///     message names the variable and never the value.
    /// </exception>
    public static IReadOnlyList<JwtKey> Parse(string? secret, string? previousSecret)
    {
        if (!IsSet(secret))
        {
            if (IsSet(previousSecret))
            {
                throw new InvalidOperationException(
                    $"Environment variable '{PreviousSecretVariable}' is set without '{SecretVariable}'. " +
                    "Set both or neither. Refusing to start.");
            }

            return [];
        }

        EnsureUsable(SecretVariable, secret);

        List<JwtKey> keys = [new(CurrentKeyId, secret)];

        if (IsSet(previousSecret) && previousSecret != secret)
        {
            EnsureUsable(PreviousSecretVariable, previousSecret);
            keys.Add(new JwtKey(PreviousKeyId, previousSecret));
        }

        return keys;
    }

    /// <summary>Whether a secret is configured at all: neither blank nor an empty quoted value.</summary>
    private static bool IsSet([NotNullWhen(true)] string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        !string.Equals(value.Trim(), EmptyQuotedValue, StringComparison.Ordinal);

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
