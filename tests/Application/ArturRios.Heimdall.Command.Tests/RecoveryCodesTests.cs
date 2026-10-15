using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ArturRios.Heimdall.Command.Services;
using ArturRios.Util.Test.Attributes;

namespace ArturRios.Heimdall.Command.Tests;

// Unit tests for RecoveryCodes (FR-2F-05, FR-2F-12, FR-2F-20): the shape of an issued code, the
// digest stored for it, and how a presented code is normalised before it is hashed — hyphens,
// whitespace and case ignored, Crockford's aliases applied to the sixteen-character shape only, the
// eight-character shape issued before still matching as it was stored, and every other length
// matching nothing.
public class RecoveryCodesTests
{
    private static readonly Regex IssuedShape =
        new("^[0-9A-HJKMNP-TV-Z]{4}-[0-9A-HJKMNP-TV-Z]{4}-[0-9A-HJKMNP-TV-Z]{4}-[0-9A-HJKMNP-TV-Z]{4}$");

    private static byte[] Sha256(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));

    [UnitFact]
    public void GivenNothing_WhenIssued_ThenTenDistinctCodesInGroupedCrockfordShape()
    {
        // When
        var issued = RecoveryCodes.Issue();

        // Then
        Assert.Equal(10, issued.Count);
        Assert.All(issued, code => Assert.Matches(IssuedShape, code.Code));
        Assert.Equal(10, issued.Select(code => code.Code).Distinct().Count());
    }

    [UnitFact]
    public void GivenAnIssuedCode_WhenIssued_ThenItsDigestIsSha256OfTheSixteenCharactersWithoutHyphens()
    {
        // When
        var issued = RecoveryCodes.Issue()[0];

        // Then
        Assert.Equal(Sha256(issued.Code.Replace("-", string.Empty)), issued.CodeHash);
    }

    [UnitFact]
    public void GivenManyIssuedCodes_WhenTheirCharactersAreCounted_ThenEveryCharacterOfTheAlphabetAppearsAndNoOther()
    {
        // Given 3,200 characters: the chance that one of the 32 never appears is about e^-100
        var characters = Enumerable.Range(0, 20)
            .SelectMany(_ => RecoveryCodes.Issue())
            .SelectMany(code => code.Code.Replace("-", string.Empty))
            .ToHashSet();

        // Then
        Assert.Equal(RecoveryCodes.Alphabet.ToHashSet(), characters);
        Assert.Equal(32, RecoveryCodes.Alphabet.Length);
        Assert.DoesNotContain('I', RecoveryCodes.Alphabet);
        Assert.DoesNotContain('L', RecoveryCodes.Alphabet);
        Assert.DoesNotContain('O', RecoveryCodes.Alphabet);
        Assert.DoesNotContain('U', RecoveryCodes.Alphabet);
    }

    [UnitFact]
    public void GivenAnIssuedCode_WhenPresentedAsShown_ThenItsDigestIsTheStoredOne()
    {
        // Given
        var issued = RecoveryCodes.Issue()[0];

        // When
        var digest = RecoveryCodes.DigestOf(issued.Code);

        // Then
        Assert.Equal(issued.CodeHash, digest);
    }

    [UnitFact]
    public void GivenAnIssuedCode_WhenPresentedWithoutHyphens_ThenItsDigestIsTheStoredOne()
    {
        // Given
        var issued = RecoveryCodes.Issue()[0];

        // When
        var digest = RecoveryCodes.DigestOf(issued.Code.Replace("-", string.Empty));

        // Then
        Assert.Equal(issued.CodeHash, digest);
    }

    [UnitFact]
    public void GivenAnIssuedCode_WhenPresentedWithSpacesForHyphensAndAround_ThenItsDigestIsTheStoredOne()
    {
        // Given
        var issued = RecoveryCodes.Issue()[0];

        // When
        var digest = RecoveryCodes.DigestOf($"  {issued.Code.Replace('-', ' ')}\t\n");

        // Then
        Assert.Equal(issued.CodeHash, digest);
    }

    [UnitFact]
    public void GivenAnIssuedCode_WhenPresentedInLowerCaseWithHyphensMisplaced_ThenItsDigestIsTheStoredOne()
    {
        // Given
        var issued = RecoveryCodes.Issue()[0];
        var canonical = issued.Code.Replace("-", string.Empty);

        // When
        var digest = RecoveryCodes.DigestOf($"{canonical[..2]}-{canonical[2..11]}--{canonical[11..]}".ToLowerInvariant());

        // Then
        Assert.Equal(issued.CodeHash, digest);
    }

    [UnitFact]
    public void GivenACodeHoldingZerosAndOnes_WhenTypedWithTheLettersTheyAreMisreadAs_ThenItsDigestIsTheStoredOne()
    {
        // Given a canonical code with 0 and 1 in it, stored as issued
        var stored = RecoveryCodes.DigestOf("0000-1111-ABCD-EF01");

        // When the zeros are typed as O or o and the ones as I, i, L or l
        var typed = new[]
        {
            RecoveryCodes.DigestOf("OOOO-IIII-ABCD-EFO1"),
            RecoveryCodes.DigestOf("oooo-llll-abcd-efoi"),
            RecoveryCodes.DigestOf("0o0O-iIlL-ABCD-EF0L")
        };

        // Then
        Assert.Equal(Sha256("00001111ABCDEF01"), stored);
        Assert.All(typed, digest => Assert.Equal(stored, digest));
    }

    [UnitFact]
    public void GivenSixteenCharactersHoldingAU_WhenPresented_ThenItMatchesNothing()
    {
        // When — U is the one letter Crockford's alphabet leaves out without an alias
        var digest = RecoveryCodes.DigestOf("UUUU-0000-1111-2222");

        // Then
        Assert.Null(digest);
    }

    [UnitFact]
    public void GivenSixteenCharactersHoldingPunctuation_WhenPresented_ThenItMatchesNothing()
    {
        // When
        var digest = RecoveryCodes.DigestOf("ABCD-0000-1111-22_2");

        // Then
        Assert.Null(digest);
    }

    [UnitFact]
    public void GivenALegacyCode_WhenPresentedAsIssued_ThenItsDigestIsSha256OfTheHyphenatedUppercaseForm()
    {
        // Given a code of the shape issued before 80-bit codes, stored as its exact text
        const string legacy = "A1B2-C3D4";

        // When
        var digest = RecoveryCodes.DigestOf(legacy);

        // Then
        Assert.Equal(Sha256(legacy), digest);
    }

    [UnitFact]
    public void GivenALegacyCode_WhenPresentedInLowerCaseWithoutTheHyphen_ThenItsDigestIsTheStoredOne()
    {
        // When
        var digests = new[]
        {
            RecoveryCodes.DigestOf("a1b2c3d4"),
            RecoveryCodes.DigestOf(" a1b2 c3d4 "),
            RecoveryCodes.DigestOf("A1-B2-C3-D4")
        };

        // Then
        Assert.All(digests, digest => Assert.Equal(Sha256("A1B2-C3D4"), digest));
    }

    [UnitFact]
    public void GivenALegacyCodeHoldingOIAndL_WhenPresented_ThenTheLettersAreKept()
    {
        // Given — the old alphabet had every letter, so an O, an I or an L in such a code is real
        const string legacy = "OIL0-LIO1";

        // When
        var digest = RecoveryCodes.DigestOf(legacy.ToLowerInvariant());

        // Then
        Assert.Equal(Sha256(legacy), digest);
        Assert.NotEqual(Sha256("0110-1101"), digest);
    }

    [UnitFact]
    public void GivenALegacyLengthCodeHoldingPunctuation_WhenPresented_ThenItMatchesNothing()
    {
        // When
        var digest = RecoveryCodes.DigestOf("A1B2_C3D");

        // Then
        Assert.Null(digest);
    }

    [UnitFact]
    public void GivenAnyOtherLength_WhenPresented_ThenItMatchesNothing()
    {
        // When — lengths around both shapes, hyphens aside
        var digests = new[]
        {
            RecoveryCodes.DigestOf("A1B2-C3D"),
            RecoveryCodes.DigestOf("A1B2-C3D4E"),
            RecoveryCodes.DigestOf("0000-1111-ABCD-EF0"),
            RecoveryCodes.DigestOf("0000-1111-ABCD-EF012"),
            RecoveryCodes.DigestOf("123456"),
            RecoveryCodes.DigestOf("----"),
            RecoveryCodes.DigestOf("   "),
            RecoveryCodes.DigestOf(string.Empty),
            RecoveryCodes.DigestOf(null)
        };

        // Then
        Assert.All(digests, Assert.Null);
    }

    [UnitFact]
    public void GivenNonAsciiCharacters_WhenPresented_ThenItMatchesNothing()
    {
        // When — a dotless i would upper-case to I, and a full-width digit looks like one
        var digests = new[]
        {
            RecoveryCodes.DigestOf("ıııı-0000-1111-2222"),
            RecoveryCodes.DigestOf("１１１１-0000-1111-2222"),
            RecoveryCodes.DigestOf("A1B2-C3Dé")
        };

        // Then
        Assert.All(digests, Assert.Null);
    }
}
