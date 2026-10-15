using ArturRios.Heimdall.Command.Services;
using ArturRios.Util.Test.Attributes;

namespace ArturRios.Heimdall.Command.Tests;

// Unit tests for ApplicationCredentialSecret (UC-53, TH-33): a secret is `hds_` plus 32 random bytes,
// base64url-encoded without padding, so it is recognisable in a leaked log and safe in a URL or an
// environment file; it is stored only as its SHA-256.
public class ApplicationCredentialSecretTests
{
    [UnitFact]
    public void GivenANewSecret_WhenInspected_ThenItIsPrefixedAndCarriesThirtyTwoBytes()
    {
        // Given / When
        var secret = ApplicationCredentialSecret.New();

        // Then — 4 prefix characters + 43 base64url characters for 32 bytes
        Assert.StartsWith(ApplicationCredentialSecret.Prefix, secret);
        Assert.Equal(47, secret.Length);
        Assert.Matches("^hds_[A-Za-z0-9_-]{43}$", secret);
    }

    [UnitFact]
    public void GivenTwoNewSecrets_WhenCompared_ThenTheyDiffer()
    {
        // Given / When
        var first = ApplicationCredentialSecret.New();
        var second = ApplicationCredentialSecret.New();

        // Then
        Assert.NotEqual(first, second);
    }

    [UnitFact]
    public void GivenASecret_WhenHashed_ThenItIsLowercaseSha256HexAndStable()
    {
        // Given
        var secret = ApplicationCredentialSecret.New();

        // When
        var hash = ApplicationCredentialSecret.HashOf(secret);

        // Then
        Assert.Matches("^[0-9a-f]{64}$", hash);
        Assert.Equal(hash, ApplicationCredentialSecret.HashOf(secret));
        Assert.NotEqual(hash, ApplicationCredentialSecret.HashOf(ApplicationCredentialSecret.New()));
    }
}
