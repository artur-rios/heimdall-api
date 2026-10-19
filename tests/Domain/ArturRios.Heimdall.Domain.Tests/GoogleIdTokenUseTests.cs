using System.Security.Cryptography;
using System.Text;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Util.Test.Attributes;

namespace ArturRios.Heimdall.Domain.Tests;

// Unit tests for GoogleIdTokenUse.KeyOf (FR-GO-21): a token is known by its jti when it has one, and by
// its signed part otherwise, whichever way its signature is spelled, and only ever by a digest.
public class GoogleIdTokenUseTests
{
    private static string Sha256Hex(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    [UnitFact]
    public void GivenATokenWithAnId_WhenKeyed_ThenTheKeyIsTheDigestOfTheId()
    {
        // Given / When
        var key = GoogleIdTokenUse.KeyOf("jti-1", "header.payload.signature");

        // Then
        Assert.Equal(Sha256Hex("jti-1"), key);
        Assert.Equal(64, key.Length);
    }

    [UnitFact]
    public void GivenTwoTokensSharingAnId_WhenKeyed_ThenTheyShareAKey()
    {
        // Given / When
        var first = GoogleIdTokenUse.KeyOf("jti-1", "token-a");
        var second = GoogleIdTokenUse.KeyOf("jti-1", "token-b");

        // Then
        Assert.Equal(first, second);
    }

    [UnitTheory]
    [InlineData(null)]
    [InlineData("")]
    public void GivenATokenWithoutAnId_WhenKeyed_ThenTheKeyIsTheDigestOfItsSignedPart(string? tokenId)
    {
        // Given / When
        var key = GoogleIdTokenUse.KeyOf(tokenId, "header.payload.signature");

        // Then
        Assert.Equal(Sha256Hex("header.payload"), key);
        Assert.NotEqual(GoogleIdTokenUse.KeyOf(tokenId, "header.another-payload.signature"), key);
    }

    [UnitFact]
    public void GivenATokenWithoutAnIdRespelled_WhenKeyed_ThenEverySpellingSharesAKey()
    {
        // Given an RS256-sized signature, whose last base64url character carries four unused bits,
        // and the same signature with those bits set — one value, two spellings
        var signature = Base64Url(Enumerable.Range(0, 256).Select(i => (byte)(i * 7)).ToArray());
        var respelled = signature[..^1] + Alphabet[Alphabet.IndexOf(signature[^1]) ^ 0b1111];
        var token = $"eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiIxIn0.{signature}";
        var respelledToken = $"eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiIxIn0.{respelled}";

        // When
        var key = GoogleIdTokenUse.KeyOf(null, token);
        var respelledKey = GoogleIdTokenUse.KeyOf(null, respelledToken);
        var paddedKey = GoogleIdTokenUse.KeyOf(null, $" {token}\n");

        // Then
        Assert.NotEqual(token, respelledToken);
        Assert.Equal(Decode(signature), Decode(respelled));
        Assert.Equal(key, respelledKey);
        Assert.Equal(key, paddedKey);
    }

    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    // Decoded bit by bit, so that the unused trailing bits are dropped as a lenient decoder drops
    // them, rather than relying on how one library treats them.
    private static byte[] Decode(string base64Url)
    {
        var bits = string.Concat(
            base64Url.Select(c => Convert.ToString(Alphabet.IndexOf(c), 2).PadLeft(6, '0')));

        return Enumerable.Range(0, bits.Length / 8)
            .Select(i => Convert.ToByte(bits.Substring(i * 8, 8), 2))
            .ToArray();
    }
}
