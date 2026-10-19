using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ArturRios.Heimdall.WebApi.Security;
using ArturRios.Heimdall.WebApi.Tests.Support;
using ArturRios.Jwt;
using ArturRios.Util.Test.Attributes;
using Microsoft.IdentityModel.JsonWebTokens;

namespace ArturRios.Heimdall.WebApi.Tests;

// Unit tests for TokenSigningKeys: HEIMDALL_AUTH_SIGNING_KEY must be the base64 of a P-256 private
// key's PEM, or start-up stops with a message that names the variable and never the value. Its kid
// is the key's RFC 7638 thumbprint. An unset, blank or empty-quoted previous key means no rotation
// is in progress; any other previous key is held to the same rules and only ever verifies.
public class TokenSigningKeysTests
{
    private const string LegacyKeyId = "legacy";
    private const string LegacySecret = "unit-tests-legacy-hs256-signing-secret-0123456789";

    // The unusable values, by name, so that no test name carries key material. Each is built when
    // its case runs.
    private static readonly Dictionary<string, Func<string?>> UnusableValues = new()
    {
        ["null"] = () => null,
        ["empty"] = () => "",
        ["blank"] = () => "   ",
        ["empty quoted"] = () => "\"\"",
        ["not base64"] = () => "not base64!",
        ["base64 of text"] = () => UnitSigningKeys.EnvValueOf("hello"),
        ["raw PEM"] = () =>
        {
            using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);

            return ecdsa.ExportPkcs8PrivateKeyPem();
        },
        ["P-384 PKCS#8 PEM"] = () =>
        {
            using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP384);

            return UnitSigningKeys.EnvValueOf(ecdsa.ExportPkcs8PrivateKeyPem());
        },
        ["RSA-2048 PKCS#8 PEM"] = () =>
        {
            using var rsa = RSA.Create(2048);

            return UnitSigningKeys.EnvValueOf(rsa.ExportPkcs8PrivateKeyPem());
        },
        ["P-256 public key PEM"] = () =>
        {
            using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);

            return UnitSigningKeys.EnvValueOf(ecdsa.ExportSubjectPublicKeyInfoPem());
        },
        ["encrypted PKCS#8 PEM"] = () =>
        {
            using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);

            return UnitSigningKeys.EnvValueOf(ecdsa.ExportEncryptedPkcs8PrivateKeyPem(
                "pw", new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 1000)));
        },
        ["base64 of DER"] = () =>
        {
            using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);

            return Convert.ToBase64String(ecdsa.ExportPkcs8PrivateKey());
        }
    };

    [UnitFact]
    public void GivenABase64Pkcs8P256Key_WhenParsing_ThenItSignsAndItsIdIsItsThumbprint()
    {
        // Given
        var (key, value) = UnitSigningKeys.Create();

        // When
        var keys = TokenSigningKeys.Parse(value, null);

        // Then
        Assert.True(keys.Current.CanSign);
        Assert.Equal(JwtAlgorithms.ES256, keys.Current.Algorithm);
        Assert.Equal(key.Id, keys.Current.Id);
        Assert.Equal(keys.Current.Thumbprint, keys.Current.Id);
        Assert.Null(keys.Previous);
        Assert.Equal([keys.Current], keys.ConfigurationKeys);
    }

    [UnitFact]
    public void GivenAKey_WhenParsing_ThenItsIdIsWhatKidPyPrints()
    {
        // Given — the canonical JWK /root/secrets/g6/kid.py hashes
        var (ecdsa, value) = UnitSigningKeys.CreateEcdsa();
        var q = ecdsa.ExportParameters(false).Q;
        var canonical = $"{{\"crv\":\"P-256\",\"kty\":\"EC\",\"x\":\"{Base64Url.EncodeToString(q.X)}\",\"y\":\"{Base64Url.EncodeToString(q.Y)}\"}}";

        // When
        var keys = TokenSigningKeys.Parse(value, null);

        // Then
        Assert.Equal(Base64Url.EncodeToString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))), keys.Current.Id);
    }

    [UnitFact]
    public void GivenSurroundingWhitespace_WhenParsing_ThenTheKeyIsRead()
    {
        // Given — what an env file or a shell may leave around the value
        var (_, value) = UnitSigningKeys.Create();

        // When
        var keys = TokenSigningKeys.Parse("  " + value + "\n", null);

        // Then
        Assert.True(keys.Current.CanSign);
    }

    [UnitTheory]
    [InlineData("null", "is unset")]
    [InlineData("empty", "is unset")]
    [InlineData("blank", "is unset")]
    [InlineData("empty quoted", "is unset")]
    [InlineData("not base64", "is not the base64 of a PEM")]
    [InlineData("base64 of text", "is not the base64 of a PEM")]
    [InlineData("raw PEM", "holds a PEM itself")]
    [InlineData("P-384 PKCS#8 PEM", "is not a P-256 private key")]
    [InlineData("RSA-2048 PKCS#8 PEM", "is not a P-256 private key")]
    [InlineData("P-256 public key PEM", "is not a P-256 private key")]
    [InlineData("encrypted PKCS#8 PEM", "is not a P-256 private key")]
    [InlineData("base64 of DER", "is not the base64 of a PEM")]
    public void GivenAnUnusableSigningKey_WhenParsing_ThenStartUpIsRefusedNamingOnlyTheVariable(
        string unusable, string refusalPhrase)
    {
        // Given
        var value = UnusableValues[unusable]();

        // When
        var refusal = Assert.Throws<InvalidOperationException>(() => TokenSigningKeys.Parse(value, null));

        // Then
        Assert.Contains("'HEIMDALL_AUTH_SIGNING_KEY'", refusal.Message);
        Assert.Contains($"'HEIMDALL_AUTH_SIGNING_KEY' {refusalPhrase}", refusal.Message);
        Assert.Contains("Refusing to start", refusal.Message);
        Assert.DoesNotContain("-----BEGIN", refusal.Message);
        if (!string.IsNullOrWhiteSpace(value) && value.Trim() != "\"\"") Assert.DoesNotContain(value.Trim(), refusal.Message);
    }

    [UnitTheory]
    [InlineData("not base64", "is not the base64 of a PEM")]
    [InlineData("base64 of text", "is not the base64 of a PEM")]
    [InlineData("raw PEM", "holds a PEM itself")]
    [InlineData("P-384 PKCS#8 PEM", "is not a P-256 private key")]
    [InlineData("RSA-2048 PKCS#8 PEM", "is not a P-256 private key")]
    [InlineData("P-256 public key PEM", "is not a P-256 private key")]
    [InlineData("encrypted PKCS#8 PEM", "is not a P-256 private key")]
    [InlineData("base64 of DER", "is not the base64 of a PEM")]
    public void GivenAnUnusablePreviousSigningKey_WhenParsing_ThenStartUpIsRefusedNamingThePreviousVariable(
        string unusable, string refusalPhrase)
    {
        // Given
        var (_, current) = UnitSigningKeys.Create();
        var value = UnusableValues[unusable]()!;

        // When
        var refusal = Assert.Throws<InvalidOperationException>(() => TokenSigningKeys.Parse(current, value));

        // Then
        Assert.Contains("'HEIMDALL_AUTH_SIGNING_KEY_PREVIOUS'", refusal.Message);
        Assert.Contains($"'HEIMDALL_AUTH_SIGNING_KEY_PREVIOUS' {refusalPhrase}", refusal.Message);
        Assert.Contains("Refusing to start", refusal.Message);
        Assert.DoesNotContain("-----BEGIN", refusal.Message);
        Assert.DoesNotContain(value.Trim(), refusal.Message);
        Assert.DoesNotContain(current, refusal.Message);
    }

    [UnitTheory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\"\"")]
    public void GivenNoPreviousKey_WhenParsing_ThenOneKeyIsPublished(string? previous)
    {
        // Given — what an unset or empty env value, quoted or not, reaches the process as
        var (_, value) = UnitSigningKeys.Create();

        // When
        var keys = TokenSigningKeys.Parse(value, previous);

        // Then
        Assert.Null(keys.Previous);
        Assert.Single(keys.ConfigurationKeys);
        Assert.Single(Regex.Matches(keys.JwksJson, "\"kid\""));
        var published = Assert.Single(JwksDocument.Parse(keys.JwksJson));
        Assert.Equal(keys.Current.Id, published.Id);
    }

    [UnitFact]
    public void GivenAPreviousKey_WhenParsing_ThenItVerifiesButNeverSigns()
    {
        // Given
        var (_, current) = UnitSigningKeys.Create();
        var (previousKey, previous) = UnitSigningKeys.Create();

        // When
        var keys = TokenSigningKeys.Parse(current, previous);

        // Then
        Assert.False(keys.Previous!.CanSign);
        Assert.Equal(previousKey.Id, keys.Previous.Id);
        Assert.Equal([keys.Current.Id, keys.Previous.Id], keys.ConfigurationKeys.Select(key => key.Id));
        Assert.Equal([keys.Current.Id, keys.Previous.Id], JwksDocument.Parse(keys.JwksJson).Select(key => key.Id));
    }

    [UnitFact]
    public void GivenAPreviousKeyEqualToTheCurrent_WhenParsing_ThenItIsIgnored()
    {
        // Given — a rotation not yet started: the same key under both variables
        var (_, value) = UnitSigningKeys.Create();

        // When
        var keys = TokenSigningKeys.Parse(value, value);

        // Then
        Assert.Null(keys.Previous);
        Assert.Single(keys.ConfigurationKeys);
        Assert.Single(Regex.Matches(keys.JwksJson, "\"kid\""));
        Assert.Single(JwksDocument.Parse(keys.JwksJson));
    }

    [UnitFact]
    public void GivenAKeySet_WhenPublished_ThenItHoldsNoPrivateMember()
    {
        // Given
        var (_, current) = UnitSigningKeys.Create();
        var (_, previous) = UnitSigningKeys.Create();

        // When
        var keys = TokenSigningKeys.Parse(current, previous);

        // Then
        Assert.DoesNotContain("\"d\"", keys.JwksJson);
    }

    [UnitFact]
    public void GivenABaseConfiguration_WhenTheKeysApply_ThenItSignsEs256WithTheCurrentKey()
    {
        // Given — a base that still carries an HS256 secret and key, which must not survive
        var (_, value) = UnitSigningKeys.Create();
        var keys = TokenSigningKeys.Parse(value, null);
        var baseConfiguration = new JwtConfiguration(3600, "i", "a", LegacySecret, [])
        {
            Keys = [new JwtKey(LegacyKeyId, LegacySecret)],
            SigningKeyId = LegacyKeyId
        };

        // When
        var c = keys.ApplyTo(baseConfiguration);

        // Then
        Assert.Equal("ES256", c.Algorithm);
        Assert.Equal(keys.Current.Id, c.SigningKeyId);
        Assert.Equal(keys.ConfigurationKeys, c.Keys);
        Assert.DoesNotContain(c.Keys, key => key.Id == LegacyKeyId);
        Assert.Equal("", c.Secret);
        Assert.Equal("i", c.Issuer);
        Assert.Equal("a", c.Audience);
        Assert.Equal(3600, c.ExpirationInSeconds);
        var token = new JsonWebToken(new JwtHandler().CreateToken(c with { Claims = new() { ["id"] = "x" } }));
        Assert.Equal(JwtAlgorithms.ES256, token.Alg);
        Assert.Equal(keys.Current.Id, token.Kid);
    }
}
