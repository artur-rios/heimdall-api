using System.Security.Cryptography;
using System.Text;

namespace ArturRios.Heimdall.Command.Services;

/// <summary>
///     Hashes the single-use tokens delivered by email — the password reset token of UC-12 and the
///     email verification token of UC-14 — so that neither is stored in a form that can be presented
///     back to the API.
/// </summary>
/// <remarks>
///     <para>
///         This exists because both were stored in plaintext (Threat Model TH-14) while the
///         second-factor recovery codes beside them were hashed, even though a reset token is the
///         stronger primitive of the two: a recovery code still requires the password, and a reset
///         token replaces it. Anyone who could read the table could complete a reset for any account
///         holding a live token, which did not break the Argon2id work protecting the password so
///         much as walk around it.
///     </para>
///     <para>
///         Unsalted SHA-256, which is the same choice already made for recovery codes and is
///         deliberate rather than an oversight. A salt defends a low-entropy secret against a
///         precomputed table; these tokens are 48 characters drawn from a CSPRNG, so there is no
///         table to precompute and nothing for a salt to add. What it costs to leave out is what
///         makes this workable at all: an unsalted digest is deterministic, so the presented token
///         can be hashed once and looked up against a unique index. A per-row salt would force a
///         scan of every live token on every attempt, because the caller presents the token alone
///         and there is nothing else to narrow the candidates by.
///     </para>
///     <para>
///         Both directions go through here so the value written at issue and the value compared at
///         presentation cannot drift apart.
///     </para>
///     <para>
///         Lowercase hex rather than the raw bytes, and that choice is about equality rather than
///         about storage. <c>byte[] == byte[]</c> is value equality once EF has translated it into
///         SQL and reference equality everywhere else, so the handler's lookup matched correctly
///         against PostgreSQL and silently matched nothing against an in-memory repository — which
///         is exactly how the unit suite reads it, and how this was caught. A string compares by
///         value in both, so the expression means one thing wherever it runs. The digest is 32 bytes
///         either way; hex spends 64 characters to remove that ambiguity.
///     </para>
/// </remarks>
public static class SingleUseTokenHash
{
    /// <summary>The length of the value <see cref="Of" /> returns: SHA-256 as lowercase hex.</summary>
    public const int Length = 64;

    /// <summary>The stored form of <paramref name="token" />.</summary>
    public static string Of(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
}
