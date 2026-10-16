using System.Security.Cryptography;
using System.Text;

namespace ArturRios.Heimdall.Command.Services;

/// <summary>A recovery code as issued: the form shown to the person once, and the digest stored for it.</summary>
/// <param name="Code">The code as displayed, <c>XXXX-XXXX-XXXX-XXXX</c>.</param>
/// <param name="CodeHash">What <c>TwoFactorRecoveryCode.CodeHash</c> stores for it.</param>
public sealed record IssuedRecoveryCode(string Code, byte[] CodeHash);

/// <summary>
///     The second-factor recovery codes of FR-2F-05 and FR-2F-12: how they are generated, how a
///     presented one is normalised, and how both are hashed. UC-37 and UC-40 issue them and
///     <see cref="TwoFactorFactorVerifier" /> matches them, and all three go through here so the
///     value written at issue and the value compared at presentation cannot drift apart.
/// </summary>
/// <remarks>
///     <para>
///         <b>Shape.</b> Ten random bytes from <see cref="RandomNumberGenerator" />, 80 bits, written
///         as sixteen characters of Crockford's base32 (<see cref="Alphabet" />: the digits and the
///         uppercase letters without I, L, O and U) and shown in four groups of four,
///         <c>XXXX-XXXX-XXXX-XXXX</c>. Crockford's rather than RFC 4648's because it leaves out the
///         letters people misread for digits and reads them back as those digits instead: a code
///         typed with an O, an I or an L still matches.
///     </para>
///     <para>
///         <b>What a presented code may look like.</b> Hyphens and whitespace are ignored wherever
///         they fall, and case is ignored. What remains is sixteen characters for a code issued
///         now, or eight for one issued before (two groups of four from <c>[A-Z0-9]</c>, stored as
///         the digest of the exact <c>XXXX-XXXX</c> uppercase form). Those keep matching until they
///         are used or the set is regenerated (FR-2F-20). Crockford's aliases apply to the sixteen
///         only: an older code may really contain an O, an I or an L, and mapping it would make it
///         miss. Any other length matches nothing.
///     </para>
///     <para>
///         <b>Why SHA-256 is enough.</b> A recovery code is not chosen by a person; it is 80 bits
///         drawn uniformly at random, so there is no likely subset for a guesser to start with.
///         Finding one particular code from its digest takes 2^79 SHA-256 evaluations on average,
///         millions of years at the 10^10 a second of a current GPU. The digest is unsalted, so one
///         pass over the space tests every stored code at once, which divides that work by the
///         number of codes stored: at a million codes it is still about 2^60 evaluations — years of
///         one GPU — to recover a single code of some account nobody chose, which is then good only
///         beside that account's password. A slow hash would add little at this entropy and would
///         cost a derivation per unused code on every presentation, since a code is matched against
///         its configuration's whole unused set. A keyed hash, with a pepper held outside the
///         database, is the step to take should the table ever grow by orders of magnitude. The
///         eight-character codes issued before carry fewer bits; regenerating replaces them (UC-40).
///     </para>
/// </remarks>
public static class RecoveryCodes
{
    /// <summary>How many codes a set holds (FR-2F-05).</summary>
    public const int Count = 10;

    /// <summary>Crockford's base32 alphabet: each character carries five bits.</summary>
    public const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    /// <summary>The characters in a code, hyphens aside.</summary>
    public const int Length = 16;

    /// <summary>The characters in a code issued before 80-bit codes, hyphen aside.</summary>
    public const int LegacyLength = 8;

    private const int RandomBytes = 10;
    private const int GroupLength = 4;
    private const int BitsPerCharacter = 5;

    /// <summary>A fresh set of <see cref="Count" /> codes, each with the digest to store for it.</summary>
    public static List<IssuedRecoveryCode> Issue()
    {
        var codes = new List<IssuedRecoveryCode>(Count);

        for (var i = 0; i < Count; i++)
        {
            var canonical = NewCanonicalCode();

            codes.Add(new IssuedRecoveryCode(Display(canonical), Digest(canonical)));
        }

        return codes;
    }

    /// <summary>
    ///     The digest <paramref name="presented" /> would be stored under, or <c>null</c> when it
    ///     cannot be a recovery code of either shape and so matches nothing. See the remarks for
    ///     what is normalised.
    /// </summary>
    public static byte[]? DigestOf(string? presented)
    {
        if (presented is null)
        {
            return null;
        }

        var stripped = new StringBuilder(presented.Length);

        foreach (var character in presented)
        {
            if (character == '-' || char.IsWhiteSpace(character))
            {
                continue;
            }

            // Neither alphabet has anything outside ASCII, and upper-casing one could turn it into
            // a letter that is in them (a dotless i into I, say).
            if (!char.IsAscii(character))
            {
                return null;
            }

            stripped.Append(char.ToUpperInvariant(character));
        }

        return stripped.Length switch
        {
            Length => CurrentDigest(stripped.ToString()),
            LegacyLength => LegacyDigest(stripped.ToString()),
            _ => null
        };
    }

    /// <summary>
    ///     <paramref name="bytes" /> in <see cref="Alphabet" />, five bits a character, most
    ///     significant first: the canonical form of a code. Ten bytes give the sixteen characters of
    ///     <see cref="Length" /> exactly. Public so the mapping can be pinned by known answers; issuing
    ///     goes through <see cref="Issue" />.
    /// </summary>
    /// <exception cref="ArgumentException">The length is not a multiple of five bytes.</exception>
    public static string Encode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length % 5 != 0)
        {
            throw new ArgumentException("Base32 without padding needs a multiple of five bytes.", nameof(bytes));
        }

        var code = new StringBuilder(bytes.Length * 8 / BitsPerCharacter);
        var buffer = 0;
        var bits = 0;

        foreach (var value in bytes)
        {
            buffer = (buffer << 8) | value;
            bits += 8;

            while (bits >= BitsPerCharacter)
            {
                bits -= BitsPerCharacter;
                code.Append(Alphabet[(buffer >> bits) & 0b11111]);
            }
        }

        return code.ToString();
    }

    /// <summary>Sixteen characters for <see cref="RandomBytes" /> random bytes.</summary>
    private static string NewCanonicalCode()
    {
        Span<byte> random = stackalloc byte[RandomBytes];
        RandomNumberGenerator.Fill(random);

        return Encode(random);
    }

    /// <summary><c>XXXX-XXXX-XXXX-XXXX</c>.</summary>
    private static string Display(string canonical) =>
        string.Join('-', Enumerable.Range(0, Length / GroupLength)
            .Select(group => canonical.Substring(group * GroupLength, GroupLength)));

    /// <summary>
    ///     A sixteen-character code: Crockford's decode aliases applied (O reads as 0, I and L as 1),
    ///     then hashed in that canonical form. A U, or anything else outside the alphabet, matches
    ///     nothing.
    /// </summary>
    private static byte[]? CurrentDigest(string stripped)
    {
        var canonical = new StringBuilder(Length);

        foreach (var character in stripped)
        {
            var decoded = character switch
            {
                'O' => '0',
                'I' or 'L' => '1',
                _ => character
            };

            if (!Alphabet.Contains(decoded))
            {
                return null;
            }

            canonical.Append(decoded);
        }

        return Digest(canonical.ToString());
    }

    /// <summary>
    ///     An eight-character code issued before: rebuilt as the exact <c>XXXX-XXXX</c> it was stored
    ///     as, with no aliases, since its alphabet had every letter.
    /// </summary>
    private static byte[]? LegacyDigest(string stripped) =>
        stripped.All(char.IsAsciiLetterOrDigit)
            ? Digest($"{stripped[..GroupLength]}-{stripped[GroupLength..]}")
            : null;

    private static byte[] Digest(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));
}
