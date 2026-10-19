using System.Security.Cryptography;
using System.Text;
using ArturRios.Jwt;

namespace ArturRios.Heimdall.WebApi.Security;

/// <summary>
///     The ES256 key every token is signed with, and the previous one during a rotation, read once at
///     start-up from <c>HEIMDALL_AUTH_SIGNING_KEY</c> and <c>HEIMDALL_AUTH_SIGNING_KEY_PREVIOUS</c>, with
///     the key set (JWKS) that publishes their public halves.
/// </summary>
/// <remarks>
///     <para>
///         <b>The format.</b> Each variable holds the base64 (standard alphabet, one line) of a PKCS#8
///         PEM P-256 private key: what <c>base64 -w0 signing.pem</c> prints for the file
///         <c>openssl genpkey -algorithm EC -pkeyopt ec_paramgen_curve:P-256</c> writes. The PEM is
///         base64-encoded because its line breaks do not survive every env file and secret store. A
///         value that is not that, holds the PEM itself, or holds any key but a P-256 private one stops
///         start-up, naming the variable and never any part of its value.
///     </para>
///     <para>
///         <b>The <c>kid</c>.</b> A key's id is its RFC 7638 thumbprint (<see cref="JwtKey.Id" />), derived
///         from the public half and never configured, so it names the same key in every token and in the
///         published key set.
///     </para>
///     <para>
///         <b>An empty previous key is no previous key.</b> Unset, blank, or two double quotes
///         (<c>""</c>, an env file's empty quoted value with its quotes intact) all mean "no rotation in
///         progress". A previous key equal to the current one is ignored too. Any other previous key is
///         held to the same rules as the current one, and only its public half is kept: it verifies the
///         tokens it signed before the rotation and never signs another.
///     </para>
/// </remarks>
public sealed class TokenSigningKeys
{
    /// <summary>The variable carrying the key new tokens are signed with. Required.</summary>
    public const string SigningKeyVariable = "HEIMDALL_AUTH_SIGNING_KEY";

    /// <summary>The variable carrying the key a rotation has moved off but not yet withdrawn. Optional.</summary>
    public const string PreviousSigningKeyVariable = "HEIMDALL_AUTH_SIGNING_KEY_PREVIOUS";

    /// <summary>The value that means "none": an env file's empty quoted value with its quotes intact.</summary>
    private const string EmptyQuotedValue = "\"\"";

    /// <summary>What every PEM begins with, and what no refusal message ever contains.</summary>
    private const string PemBoundary = "-----BEGIN";

    private const string GenerateCommand =
        "'openssl genpkey -algorithm EC -pkeyopt ec_paramgen_curve:P-256 -out signing.pem'";

    private TokenSigningKeys(JwtKey current, JwtKey? previous)
    {
        Current = current;
        Previous = previous;
        ConfigurationKeys = previous is null ? [current] : [current, previous];
        JwksJson = JwksDocument.From(ConfigurationKeys);
    }

    /// <summary>The key new tokens are signed with, holding its private half.</summary>
    public JwtKey Current { get; }

    /// <summary>
    ///     The public half of the previous key, which verifies but never signs; <see langword="null" />
    ///     when no rotation is in progress.
    /// </summary>
    public JwtKey? Previous { get; }

    /// <summary>The keys tokens are accepted with: the current one first, then the previous one.</summary>
    public IReadOnlyList<JwtKey> ConfigurationKeys { get; }

    /// <summary>The key set publishing <see cref="ConfigurationKeys" />' public halves, built once.</summary>
    public string JwksJson { get; }

    /// <summary>Reads the keys from the process environment.</summary>
    /// <exception cref="InvalidOperationException">As <see cref="Parse" />.</exception>
    public static TokenSigningKeys FromEnvironment() =>
        Parse(Environment.GetEnvironmentVariable(SigningKeyVariable),
            Environment.GetEnvironmentVariable(PreviousSigningKeyVariable));

    /// <summary>Reads the keys from raw variable values.</summary>
    /// <exception cref="InvalidOperationException">
    ///     <paramref name="signingKey" /> is unset, or either value is not the base64 of a P-256 private
    ///     key's PEM. The message names the variable and never the value.
    /// </exception>
    public static TokenSigningKeys Parse(string? signingKey, string? previousSigningKey)
    {
        var current = IsSet(signingKey)
            ? Read(SigningKeyVariable, signingKey!.Trim())
            : throw new InvalidOperationException(
                $"Environment variable '{SigningKeyVariable}' is unset. Heimdall signs its tokens with an " +
                $"ES256 key: generate one with {GenerateCommand} and set the variable to the output of " +
                "'base64 -w0 signing.pem'. Refusing to start.");

        var previous = IsSet(previousSigningKey)
            ? Read(PreviousSigningKeyVariable, previousSigningKey!.Trim()).ToPublicKey()
            : null;

        return new TokenSigningKeys(current, previous?.Id == current.Id ? null : previous);
    }

    /// <summary>
    ///     <paramref name="baseConfiguration" /> signing ES256 with <see cref="Current" /> and accepting
    ///     <see cref="ConfigurationKeys" />, with no secret.
    /// </summary>
    public JwtConfiguration ApplyTo(JwtConfiguration baseConfiguration) =>
        baseConfiguration with
        {
            Algorithm = JwtAlgorithms.ES256,
            Keys = ConfigurationKeys,
            SigningKeyId = Current.Id,
            Secret = ""
        };

    /// <summary>Whether a value is configured at all: neither blank nor an empty quoted value.</summary>
    private static bool IsSet(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        !string.Equals(value.Trim(), EmptyQuotedValue, StringComparison.Ordinal);

    private static JwtKey Read(string variable, string value)
    {
        if (value.Contains(PemBoundary, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Environment variable '{variable}' holds a PEM itself. Set it to the base64 of the PEM, on " +
                "one line: 'base64 -w0 signing.pem'. Refusing to start.");
        }

        var decoded = new byte[value.Length];

        try
        {
            if (!Convert.TryFromBase64String(value, decoded, out var length))
            {
                throw NotBase64OfAPem(variable);
            }

            var pem = Encoding.UTF8.GetString(decoded, 0, length);

            if (!pem.Contains(PemBoundary, StringComparison.Ordinal))
            {
                throw NotBase64OfAPem(variable);
            }

            try
            {
                return JwtKey.FromEcPrivateKeyPem(pem);
            }
            catch (ArgumentException)
            {
                // The library's message names no part of the key either, but this one names the
                // variable, which is what an operator needs.
                throw new InvalidOperationException(
                    $"Environment variable '{variable}' is not a P-256 private key. Generate one with " +
                    $"{GenerateCommand}. Refusing to start.");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(decoded);
        }
    }

    private static InvalidOperationException NotBase64OfAPem(string variable) =>
        new($"Environment variable '{variable}' is not the base64 of a PEM. Set it to the output of " +
            "'base64 -w0 signing.pem'. Refusing to start.");
}
