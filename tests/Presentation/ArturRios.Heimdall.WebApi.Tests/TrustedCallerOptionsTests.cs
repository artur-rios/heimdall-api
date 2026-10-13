using ArturRios.Heimdall.WebApi.Security;
using ArturRios.Util.Test.Attributes;

namespace ArturRios.Heimdall.WebApi.Tests;

// Unit tests for TrustedCallerOptions: the keys a client system presents to have its end users' own
// addresses counted by the anonymous-auth rate limiter. They are secrets, so a short one is refused
// at start-up rather than trusted, and the refusal must not print it.
public class TrustedCallerOptionsTests
{
    private const string KeyA = "caller-key-A-0123456789abcdefghijklmn";
    private const string KeyB = "caller-key-B-0123456789abcdefghijklmn";

    [UnitTheory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" , ,, ")]
    public void GivenNothingConfigured_WhenParsing_ThenNoCallerIsTrusted(string? value)
    {
        var options = TrustedCallerOptions.Parse(value);

        Assert.False(options.Configured);
        Assert.Empty(options.Keys);
        Assert.False(options.Matches(KeyA));
    }

    [UnitFact]
    public void GivenOneKey_WhenParsing_ThenItIsTrusted()
    {
        var options = TrustedCallerOptions.Parse(KeyA);

        Assert.True(options.Configured);
        Assert.Equal([KeyA], options.Keys);
    }

    [UnitFact]
    public void GivenSeveralKeysWithSpacesAndEmptyEntries_WhenParsing_ThenEntriesAreTrimmedAndEmptiesDropped()
    {
        var options = TrustedCallerOptions.Parse($"  {KeyA} ,, {KeyB}  ,");

        Assert.Equal([KeyA, KeyB], options.Keys);
    }

    [UnitFact]
    public void GivenAKeyOfExactlyTheMinimumLength_WhenParsing_ThenItIsAccepted()
    {
        var key = new string('k', TrustedCallerOptions.MinimumKeyLength);

        var options = TrustedCallerOptions.Parse(key);

        Assert.Equal([key], options.Keys);
    }

    [UnitFact]
    public void GivenAKeyOneCharacterShort_WhenParsing_ThenStartUpIsRefused()
    {
        var key = new string('k', TrustedCallerOptions.MinimumKeyLength - 1);

        var error = Assert.Throws<InvalidOperationException>(() => TrustedCallerOptions.Parse(key));

        Assert.Contains(TrustedCallerOptions.KeysVariable, error.Message);
        Assert.Contains(TrustedCallerOptions.MinimumKeyLength.ToString(), error.Message);
    }

    [UnitFact]
    public void GivenOneShortKeyAmongGoodOnes_WhenParsing_ThenStartUpIsRefusedWithoutPrintingTheKey()
    {
        const string shortKey = "short-secret-value";

        var error = Assert.Throws<InvalidOperationException>(
            () => TrustedCallerOptions.Parse($"{KeyA},{shortKey},{KeyB}"));

        // The entry is named by position, never by value: it is a secret, and the message reaches the log.
        Assert.DoesNotContain(shortKey, error.Message);
        Assert.DoesNotContain(KeyA, error.Message);
        Assert.Contains("2", error.Message);
    }

    [UnitFact]
    public void GivenTwoKeys_WhenMatching_ThenEitherIsAcceptedAndNothingElse()
    {
        var options = TrustedCallerOptions.Parse($"{KeyA},{KeyB}");

        Assert.True(options.Matches(KeyA));
        Assert.True(options.Matches(KeyB));
        Assert.False(options.Matches(KeyA + "x"));
        Assert.False(options.Matches(KeyA[..^1]));
        Assert.False(options.Matches(KeyA.ToUpperInvariant()));
        Assert.False(options.Matches(string.Empty));
        Assert.False(options.Matches(null));
    }

    [UnitFact]
    public void GivenAKeyWithNonAsciiCharacters_WhenMatching_ThenItIsComparedByItsUtf8Bytes()
    {
        var key = new string('é', TrustedCallerOptions.MinimumKeyLength);
        var options = TrustedCallerOptions.Parse(key);

        Assert.True(options.Matches(key));
        Assert.False(options.Matches(new string('e', TrustedCallerOptions.MinimumKeyLength)));
    }
}
