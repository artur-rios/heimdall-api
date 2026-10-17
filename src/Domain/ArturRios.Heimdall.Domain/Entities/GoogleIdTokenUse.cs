using System.Security.Cryptography;
using System.Text;
using ArturRios.Data.Relational.Core.Entities;

namespace ArturRios.Heimdall.Domain.Entities;

/// <summary>
///     A Google ID token the system has accepted (FR-GO-21), kept so that the same token is refused if
///     it is presented again. <see cref="Entity{TKey}.Id" /> is the token's key — see
///     <see cref="KeyOf" /> — and nothing else about the token, or about who presented it, is stored.
/// </summary>
/// <remarks>
///     Google signs a token for an hour, and until this table existed any copy of one — read from a
///     log, a proxy or a client's storage — could sign its holder in for that long. Recording the first
///     use, in the same statement that checks for an earlier one, makes every later presentation an
///     invalid token. The row is needed only until the token expires and so could no longer be accepted
///     anyway; it is purged with the single-use tokens after the same grace (NFR-19).
/// </remarks>
public class GoogleIdTokenUse : Entity<string>
{
    /// <summary>The token's own expiry, its <c>exp</c> claim, which the purge counts from.</summary>
    public DateTime ExpiresAt { get; set; }

    /// <summary>
    ///     The key a token is recorded under: a lower-case hex SHA-256 digest of its <c>jti</c> claim,
    ///     which Google sets on the tokens it issues, or, when it carries none, of its signed part —
    ///     the <c>header.payload</c> before the last dot, surrounding whitespace aside.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The signed part rather than the whole token, because the signature segment can be spelled
    ///         more than one way. Its base64url text has unused trailing bits — four in an RS256
    ///         signature's 342 characters — which a lenient decoder ignores, so one token can verify
    ///         under several spellings, and a key taken over the whole text would count each as a new
    ///         token.
    ///         The signed part cannot be respelled without breaking the signature, which covers its
    ///         exact text, so every spelling of one token has one key.
    ///     </para>
    ///     <para>
    ///         A digest rather than the value: the key is what the row is looked up by, never what it is
    ///         read for, and a stored token, unlike its digest, would be a credential until it expired.
    ///     </para>
    /// </remarks>
    public static string KeyOf(string? tokenId, string idToken) =>
        Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes(string.IsNullOrEmpty(tokenId) ? SignedPartOf(idToken) : tokenId)));

    /// <summary>
    ///     The part of a compact JWS its signature covers: everything before the last dot, or the whole
    ///     trimmed value when there is no dot.
    /// </summary>
    private static string SignedPartOf(string idToken)
    {
        var token = idToken.Trim();
        var signatureDot = token.LastIndexOf('.');

        return signatureDot < 0 ? token : token[..signatureDot];
    }
}
