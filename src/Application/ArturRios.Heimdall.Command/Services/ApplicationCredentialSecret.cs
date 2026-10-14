using System.Security.Cryptography;

namespace ArturRios.Heimdall.Command.Services;

/// <summary>
///     Generates and hashes the secrets of UC-53 (FR-AP-10, Threat Model TH-33).
/// </summary>
/// <remarks>
///     <para>
///         <b>Shape.</b> <see cref="Prefix" /> plus 32 bytes from a cryptographic generator,
///         base64url-encoded without padding. The prefix makes a leaked secret recognisable — to a
///         person reading a log and to a secret scanner — and base64url keeps it safe in a URL, a
///         header or an environment file.
///     </para>
///     <para>
///         <b>Why a fast hash.</b> 256 random bits cannot be guessed, so a slow password hash buys
///         nothing and would put UC-56 behind the Argon2id gate the password logins share (NFR-18).
///         A credential is found by looking its hash up, so no comparison runs whose duration
///         depends on how much of a submitted secret matches.
///     </para>
/// </remarks>
public static class ApplicationCredentialSecret
{
    /// <summary>The prefix every secret starts with.</summary>
    public const string Prefix = "hds_";

    private const int SecretBytes = 32;

    /// <summary>A new secret, shown to the issuer once and never stored.</summary>
    public static string New() =>
        Prefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(SecretBytes))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    /// <summary>The value stored in <c>ApplicationCredential.SecretHash</c> and looked up by UC-56.</summary>
    public static string HashOf(string secret) => SingleUseTokenHash.Of(secret);
}
