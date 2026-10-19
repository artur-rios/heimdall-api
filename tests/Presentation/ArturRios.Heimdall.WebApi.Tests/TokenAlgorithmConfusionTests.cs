using System.Text;
using System.Text.Json;
using ArturRios.Heimdall.WebApi.Security;
using ArturRios.Heimdall.WebApi.Tests.Support;
using ArturRios.Util.Test.Attributes;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace ArturRios.Heimdall.WebApi.Tests;

// Unit tests for algorithm confusion and key substitution against Heimdall's three readers during the
// overlap (spec, "Each configuration pins its algorithm"; rulings R1, R3). Every forgery is a token the
// reader would otherwise accept — its payload, audience and kind, issued before the process started
// and live — so the only thing that can refuse it is the signature or the algorithm. The controls,
// signed correctly with the same payload, show that each reader does accept such a token.
public class TokenAlgorithmConfusionTests
{
    // Generated for these tests (openssl rand -hex 20); no environment signs with them.
    private const string TokenSecret = "unit-confusion-legacy-8c1f4e0d2b9a7f3e6c5d4b3a2f1e0d9c8b7a6f5e";
    private const string OtherSecret = "unit-confusion-fortuna-0e9d8c7b6a5f4e3d2c1b0a9f8";

    private const string FortunaIssuer = "fortuna-local";

    public static TheoryData<string, string> ReadersAndForgeries()
    {
        var data = new TheoryData<string, string>();

        foreach (var reader in Readers)
        {
            foreach (var forgery in Forgeries)
            {
                data.Add(reader, forgery);
            }
        }

        return data;
    }

    public static TheoryData<string, string> ReadersAndControls()
    {
        var data = new TheoryData<string, string>();

        foreach (var reader in Readers)
        {
            data.Add(reader, "es256");
            data.Add(reader, "legacy");
        }

        return data;
    }

    private static readonly string[] Readers =
    [
        LegacyTokenValidator.BearerReader, LegacyTokenValidator.ChallengeReader, LegacyTokenValidator.ApplicationReader
    ];

    private static readonly string[] Forgeries =
    [
        "1 hs256 keyed with the published key set",
        "2 hs256 signed with the legacy secret, naming the current kid",
        "3 es256 from a fresh key renamed to the current kid",
        "4 alg none",
        "5 hs256 under fortuna-local with another secret",
        "5b hs256 under fortuna-local with the legacy secret"
    ];

    private static UnitTokenReaders ReadersDuringTheOverlap() => new(TokenSigningSecrets.Parse(TokenSecret, null));

    private static string Forge(UnitTokenReaders readers, string reader, string forgery)
    {
        var current = readers.Keys.Current;

        return forgery[..2].Trim() switch
        {
            "1" => readers.TokenFor(reader, UnitSigningKeys.Hs256(readers.Keys.JwksJson)),
            "2" => readers.TokenFor(reader, UnitSigningKeys.Hs256(TokenSecret, current.Id)),
            "3" => readers.TokenFor(reader, UnitSigningKeys.Es256(UnitSigningKeys.Create().Key with { Id = current.Id })),
            "4" => Unsigned(readers, reader),
            "5" => readers.TokenFor(reader, UnitSigningKeys.Hs256(OtherSecret), FortunaIssuer),
            "5b" => readers.TokenFor(reader, UnitSigningKeys.Hs256(TokenSecret), FortunaIssuer),
            _ => throw new ArgumentOutOfRangeException(nameof(forgery), forgery, null)
        };
    }

    // The reader's own payload under {"alg":"none"}, with an empty signature.
    private static string Unsigned(UnitTokenReaders readers, string reader)
    {
        var (claims, audience) = readers.PayloadFor(reader);
        var payload = new Dictionary<string, object>(claims)
        {
            [JwtRegisteredClaimNames.Iss] = readers.Configuration.Issuer,
            [JwtRegisteredClaimNames.Aud] = audience,
            [JwtRegisteredClaimNames.Iat] = new DateTimeOffset(readers.At(TimeSpan.FromMinutes(-3))).ToUnixTimeSeconds(),
            [JwtRegisteredClaimNames.Nbf] = new DateTimeOffset(readers.At(TimeSpan.FromMinutes(-3))).ToUnixTimeSeconds(),
            [JwtRegisteredClaimNames.Exp] = new DateTimeOffset(readers.At(TimeSpan.FromMinutes(8))).ToUnixTimeSeconds()
        };

        return $"{Base64UrlEncoder.Encode("""{"alg":"none"}""")}." +
               $"{Base64UrlEncoder.Encode(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload)))}.";
    }

    [UnitTheory]
    [MemberData(nameof(ReadersAndForgeries))]
    public async Task GivenAForgedToken_WhenAnyReaderReadsItDuringTheOverlap_ThenItIsRefused(
        string reader, string forgery)
    {
        // Given — legacy secrets configured, and a forgery otherwise valid for this reader
        var readers = ReadersDuringTheOverlap();
        var token = Forge(readers, reader, forgery);

        // When
        var refused = await readers.RefusesAsync(reader, token);

        // Then — no user, no challenge, no application, and no legacy acceptance recorded
        Assert.True(refused, $"the {reader} reader accepted forgery {forgery}");
        Assert.Empty(readers.Log.Entries);
    }

    [UnitTheory]
    [MemberData(nameof(ReadersAndForgeries))]
    public void GivenAForgedToken_WhenItsHeaderIsRead_ThenItIsTheForgeryItClaimsToBe(string reader, string forgery)
    {
        // Given / When — the precondition of the theory above: each forgery is the attack it names
        var readers = ReadersDuringTheOverlap();
        var header = new JsonWebToken(Forge(readers, reader, forgery));

        // Then
        (string Alg, string? Kid) expected = forgery[..2].Trim() switch
        {
            "1" or "5" or "5b" => ("HS256", null),
            "2" => ("HS256", readers.Keys.Current.Id),
            "3" => ("ES256", readers.Keys.Current.Id),
            "4" => ("none", null),
            _ => throw new ArgumentOutOfRangeException(nameof(forgery), forgery, null)
        };
        Assert.Equal(expected.Alg, header.Alg);
        Assert.Equal(expected.Kid, string.IsNullOrEmpty(header.Kid) ? null : header.Kid);
    }

    [UnitTheory]
    [MemberData(nameof(ReadersAndControls))]
    public async Task GivenACorrectlySignedToken_WhenEachReaderReadsItDuringTheOverlap_ThenItIsAccepted(
        string reader, string signing)
    {
        // Given — the forgeries' payload, signed ES256 with the current key, or HS256 with the legacy
        // secret and no kid as a 2.x process signed it
        var readers = ReadersDuringTheOverlap();
        var token = readers.TokenFor(reader, signing == "es256"
            ? UnitSigningKeys.Es256(readers.Keys.Current)
            : UnitSigningKeys.Hs256(TokenSecret));

        // When
        var accepted = await readers.AcceptsAsync(reader, token);

        // Then
        Assert.True(accepted, $"the {reader} reader refused the {signing} control");
    }
}
