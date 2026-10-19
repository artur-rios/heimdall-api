using ArturRios.Heimdall.WebApi.Security;
using ArturRios.Util.Test.Attributes;

namespace ArturRios.Heimdall.WebApi.Tests;

// Unit tests for TokenSigningSecrets (TH-22, spec decision 5): the HS256 secret is optional legacy,
// read only to accept tokens issued before the switch to ES256. An unset, blank or empty-quoted
// secret makes no key; a previous secret without it stops start-up. A secret that is set, and any
// previous secret, must be at least 32 bytes of ASCII, or start-up stops with a message that names
// the variable and never the value. An unset, blank or empty-quoted previous secret means no rotation
// is in progress, so no key is made of it.
public class TokenSigningSecretsTests
{
    private const string Secret = "unit-tests-current-signing-secret-0123456789";
    private const string PreviousSecret = "unit-tests-previous-signing-secret-0123456789";

    [UnitFact]
    public void GivenAUsableSecretAndNoPrevious_WhenParsed_ThenOnlyTheCurrentKeyIsMade()
    {
        // Given / When
        var keys = TokenSigningSecrets.Parse(Secret, null);

        // Then
        var key = Assert.Single(keys);
        Assert.Equal(TokenSigningSecrets.CurrentKeyId, key.Id);
        Assert.Equal(Secret, key.Secret);
    }

    [UnitFact]
    public void GivenUsableCurrentAndPreviousSecrets_WhenParsed_ThenBothKeysAreMadeCurrentFirst()
    {
        // Given / When
        var keys = TokenSigningSecrets.Parse(Secret, PreviousSecret);

        // Then
        Assert.Equal(
            [(TokenSigningSecrets.CurrentKeyId, Secret), (TokenSigningSecrets.PreviousKeyId, PreviousSecret)],
            keys.Select(key => (key.Id, key.Secret)));
    }

    [UnitTheory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\"\"")]
    [InlineData(" \"\" ")]
    public void GivenAnEmptyPreviousSecret_WhenParsed_ThenNoPreviousKeyIsMade(string previous)
    {
        // Given — what an env file's empty value, quoted or not, reaches the process as

        // When
        var keys = TokenSigningSecrets.Parse(Secret, previous);

        // Then
        Assert.Equal([TokenSigningSecrets.CurrentKeyId], keys.Select(key => key.Id));
    }

    [UnitFact]
    public void GivenAPreviousSecretEqualToTheCurrentOne_WhenParsed_ThenOnlyTheCurrentKeyIsMade()
    {
        // Given / When
        var keys = TokenSigningSecrets.Parse(Secret, Secret);

        // Then
        Assert.Single(keys);
    }

    [UnitTheory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("\"\"")]
    [InlineData(" \"\" ")]
    public void GivenNoSecret_WhenParsed_ThenNoLegacyKeyIsMade(string? secret)
    {
        // Given — unset, or what an env file's empty value, quoted or not, reaches the process as

        // When
        var keys = TokenSigningSecrets.Parse(secret, null);

        // Then
        Assert.Empty(keys);
    }

    [UnitTheory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("\"\"")]
    public void GivenAPreviousSecretWithoutTheSecret_WhenParsed_ThenStartUpIsRefusedNamingBoth(string? secret)
    {
        // Given / When
        var error = Assert.Throws<InvalidOperationException>(() => TokenSigningSecrets.Parse(secret, PreviousSecret));

        // Then
        Assert.Contains($"'{TokenSigningSecrets.PreviousSecretVariable}'", error.Message);
        Assert.Contains($"'{TokenSigningSecrets.SecretVariable}'", error.Message);
        Assert.Contains("Refusing to start", error.Message);
        Assert.DoesNotContain(PreviousSecret, error.Message);
    }

    [UnitFact]
    public void GivenASecretOfExactlyThirtyTwoBytes_WhenParsed_ThenItIsAccepted()
    {
        // Given
        var secret = new string('k', TokenSigningSecrets.MinimumBytes);

        // When
        var keys = TokenSigningSecrets.Parse(secret, null);

        // Then
        Assert.Single(keys);
    }

    [UnitFact]
    public void GivenASecretOneByteShort_WhenParsed_ThenStartUpIsRefusedWithoutTheValue()
    {
        // Given
        var secret = new string('k', TokenSigningSecrets.MinimumBytes - 1);

        // When
        var error = Assert.Throws<InvalidOperationException>(() => TokenSigningSecrets.Parse(secret, null));

        // Then
        Assert.Contains(TokenSigningSecrets.SecretVariable, error.Message);
        Assert.Contains("at least 32 bytes", error.Message);
        Assert.DoesNotContain(secret, error.Message);
    }

    [UnitFact]
    public void GivenASecretOutsideAscii_WhenParsed_ThenStartUpIsRefusedWithoutTheValue()
    {
        // Given — long enough, but 'é' would be signed as '?'
        var secret = "unit-tests-signing-secret-with-an-é-0123456789";

        // When
        var error = Assert.Throws<InvalidOperationException>(() => TokenSigningSecrets.Parse(secret, null));

        // Then
        Assert.Contains(TokenSigningSecrets.SecretVariable, error.Message);
        Assert.Contains("outside ASCII", error.Message);
        Assert.DoesNotContain(secret, error.Message);
    }

    [UnitFact]
    public void GivenAShortPreviousSecret_WhenParsed_ThenStartUpIsRefusedNamingThePreviousVariable()
    {
        // Given
        const string previous = "too-short-previous";

        // When
        var error = Assert.Throws<InvalidOperationException>(() => TokenSigningSecrets.Parse(Secret, previous));

        // Then
        Assert.Contains(TokenSigningSecrets.PreviousSecretVariable, error.Message);
        Assert.DoesNotContain(previous, error.Message);
    }

    [UnitFact]
    public void GivenAPreviousSecretOutsideAscii_WhenParsed_ThenStartUpIsRefusedNamingThePreviousVariable()
    {
        // Given
        const string previous = "unit-tests-previous-signing-secret-ñ-0123456789";

        // When
        var error = Assert.Throws<InvalidOperationException>(() => TokenSigningSecrets.Parse(Secret, previous));

        // Then
        Assert.Contains(TokenSigningSecrets.PreviousSecretVariable, error.Message);
        Assert.DoesNotContain(previous, error.Message);
    }
}
