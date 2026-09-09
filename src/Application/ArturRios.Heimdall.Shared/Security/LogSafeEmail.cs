using System.Security.Cryptography;
using System.Text;

namespace ArturRios.Heimdall.Shared.Security;

/// <summary>
///     Turns an email address into a stable reference that can be written to a log without writing
///     the address (NFR-22).
/// </summary>
/// <remarks>
///     <para>
///         <b>Why not the address.</b> A log line saying a password reset was sent to a named person
///         is personal data — the address identifies them, and the line says something about them.
///         The files are retained for a year and are read by operators, so the address would sit
///         there, in bulk, long after the operational question it answered was closed.
///     </para>
///     <para>
///         <b>Why not simply drop it.</b> Operators need to correlate: whether the three failures in
///         the log are one address retrying or three different people is the first question asked of
///         a delivery problem, and a line with no reference at all cannot answer it. A stable
///         reference keeps the correlation and drops the disclosure.
///     </para>
///     <para>
///         <b>What this is and is not.</b> It is the first twelve hex characters of the SHA-256 of
///         the normalised address. It is not reversible, and it defeats bulk harvesting: a reader of
///         the logs cannot recover the addresses. It does <em>not</em> defeat confirmation — somebody
///         who already suspects a particular address can hash it and look. That is an accepted
///         limit, and the honest way to describe this is pseudonymisation rather than anonymisation.
///         Anything stronger would need a keyed hash and a key to manage, which buys little against
///         a threat model where the log reader is an operator rather than an attacker.
///     </para>
/// </remarks>
public static class LogSafeEmail
{
    /// <summary>Hex characters kept. Twelve is 48 bits — ample against accidental collision.</summary>
    private const int ReferenceLength = 12;

    /// <summary>
    ///     A stable, non-reversible reference to <paramref name="email" />, or <c>"(none)"</c> when
    ///     there is no address to refer to.
    /// </summary>
    /// <remarks>
    ///     Normalised to lower case and trimmed first, so the same address logged from two call
    ///     sites that disagree about casing still correlates.
    /// </remarks>
    public static string Reference(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return "(none)";
        }

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(email.Trim().ToLowerInvariant()));

        return Convert.ToHexStringLower(digest)[..ReferenceLength];
    }
}
