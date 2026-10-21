using System.Text;
using System.Text.Json;
using ArturRios.Heimdall.WebApi.Security;
using ArturRios.Heimdall.WebApi.Tests.Support;
using ArturRios.Util.Test.Attributes;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace ArturRios.Heimdall.WebApi.Tests;

// Unit tests for algorithm confusion and key substitution against Heimdall's three readers (spec, "Each
// configuration pins its algorithm"; rulings R1, R3). Every forgery is a token the reader would
// otherwise accept — its payload, audience and kind, issued three minutes ago and live — so the only
// thing that can refuse it is the signature or the algorithm. The sixth is the HS256 token a
// 2.x process issued, signed with the shared secret 4.0.0 retired (TH-43): no reader accepts it any
// more. The controls, signed ES256 with the same payload, show that each reader does accept such a
// token.
public class TokenAlgorithmConfusionTests
{
    // The retired shared secret: what a 2.x process signed every token with, and what the functional
    // suite's host still has set.
    private const string TokenSecret = PostgresFixture.TokenSecret;

    // Generated for these tests (openssl rand -hex 20); no environment signs with it.
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

    public static TheoryData<string> ReadersAlone() => new(Readers);

    public static TheoryData<string, string> ReadersAndAlgSpellings()
    {
        var data = new TheoryData<string, string>();

        foreach (var reader in Readers)
        {
            foreach (var alg in AlgSpellings)
            {
                data.Add(reader, alg);
            }
        }

        return data;
    }

    private static readonly string[] Readers =
    [
        UnitTokenReaders.BearerReader, UnitTokenReaders.ChallengeReader, UnitTokenReaders.ApplicationReader
    ];

    private static readonly string[] Forgeries =
    [
        "1 hs256 keyed with the published key set",
        "2 hs256 signed with the retired secret, naming the current kid",
        "3 es256 from a fresh key renamed to the current kid",
        "4 alg none",
        "5 hs256 under fortuna-local with another secret",
        "5b hs256 under fortuna-local with the retired secret",
        "6 hs256 signed with the retired secret, as a 2.x process signed it"
    ];

    // HS256 and ES256 written in another case or with a trailing space. Algorithm names are compared
    // ordinally, so none of them is the algorithm it resembles.
    private static readonly string[] AlgSpellings = ["hs256", "HS256 ", "es256", "ES256 "];

    private static UnitTokenReaders NewReaders() => new();

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
            "6" => readers.TokenFor(reader, UnitSigningKeys.Hs256(TokenSecret)),
            _ => throw new ArgumentOutOfRangeException(nameof(forgery), forgery, null)
        };
    }

    // The reader's own payload under {"alg":"none"}, with an empty signature.
    private static string Unsigned(UnitTokenReaders readers, string reader) =>
        $"{Base64UrlEncoder.Encode("""{"alg":"none"}""")}.{Encode(Payload(readers, reader))}.";

    // The reader's own payload under a header whose alg is written exactly as given, signed with what
    // the algorithm it resembles signs with: the current key, named by its kid, for ES256, and the
    // retired secret for HS256. Written "ES256", it is the control a reader accepts.
    private static string UnderAlg(UnitTokenReaders readers, string reader, string alg)
    {
        var es256 = alg.Trim().Equals("ES256", StringComparison.OrdinalIgnoreCase);
        var credentials = es256 ? UnitSigningKeys.Es256(readers.Keys.Current) : UnitSigningKeys.Hs256(TokenSecret);
        var header = es256
            ? new Dictionary<string, object> { ["alg"] = alg, ["kid"] = readers.Keys.Current.Id, ["typ"] = "JWT" }
            : new Dictionary<string, object> { ["alg"] = alg, ["typ"] = "JWT" };
        var signingInput = $"{Encode(header)}.{Encode(Payload(readers, reader))}";
        var factory = credentials.Key.CryptoProviderFactory;
        var signer = factory.CreateForSigning(credentials.Key, credentials.Algorithm);

        try
        {
            return $"{signingInput}.{Base64UrlEncoder.Encode(signer.Sign(Encoding.ASCII.GetBytes(signingInput)))}";
        }
        finally
        {
            factory.ReleaseSignatureProvider(signer);
        }
    }

    // The reader's own payload, issued and valid from three minutes ago and expiring in eight.
    private static Dictionary<string, object> Payload(UnitTokenReaders readers, string reader)
    {
        var (claims, audience) = readers.PayloadFor(reader);

        return new Dictionary<string, object>(claims)
        {
            [JwtRegisteredClaimNames.Iss] = readers.Configuration.Issuer,
            [JwtRegisteredClaimNames.Aud] = audience,
            [JwtRegisteredClaimNames.Iat] = new DateTimeOffset(readers.At(TimeSpan.FromMinutes(-3))).ToUnixTimeSeconds(),
            [JwtRegisteredClaimNames.Nbf] = new DateTimeOffset(readers.At(TimeSpan.FromMinutes(-3))).ToUnixTimeSeconds(),
            [JwtRegisteredClaimNames.Exp] = new DateTimeOffset(readers.At(TimeSpan.FromMinutes(8))).ToUnixTimeSeconds()
        };
    }

    private static string Encode(Dictionary<string, object> part) =>
        Base64UrlEncoder.Encode(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(part)));

    [UnitTheory]
    [MemberData(nameof(ReadersAndForgeries))]
    public async Task GivenAForgedToken_WhenAnyReaderReadsIt_ThenItIsRefused(string reader, string forgery)
    {
        // Given — a forgery otherwise valid for this reader
        var readers = NewReaders();
        var token = Forge(readers, reader, forgery);

        // When
        var refused = await readers.RefusesAsync(reader, token);

        // Then — no user, no challenge, no application
        Assert.True(refused, $"the {reader} reader accepted forgery {forgery}");
    }

    [UnitTheory]
    [MemberData(nameof(ReadersAndForgeries))]
    public void GivenAForgedToken_WhenItsHeaderIsRead_ThenItIsTheForgeryItClaimsToBe(string reader, string forgery)
    {
        // Given / When — the precondition of the theory above: each forgery is the attack it names
        var readers = NewReaders();
        var header = new JsonWebToken(Forge(readers, reader, forgery));

        // Then
        (string Alg, string? Kid) expected = forgery[..2].Trim() switch
        {
            "1" or "5" or "5b" or "6" => ("HS256", null),
            "2" => ("HS256", readers.Keys.Current.Id),
            "3" => ("ES256", readers.Keys.Current.Id),
            "4" => ("none", null),
            _ => throw new ArgumentOutOfRangeException(nameof(forgery), forgery, null)
        };
        Assert.Equal(expected.Alg, header.Alg);
        Assert.Equal(expected.Kid, string.IsNullOrEmpty(header.Kid) ? null : header.Kid);
    }

    [UnitTheory]
    [MemberData(nameof(ReadersAlone))]
    public async Task GivenACorrectlySignedToken_WhenEachReaderReadsIt_ThenItIsAccepted(string reader)
    {
        // Given — the forgeries' payload, signed ES256 with the current key
        var readers = NewReaders();
        var token = readers.TokenFor(reader, UnitSigningKeys.Es256(readers.Keys.Current));

        // When
        var accepted = await readers.AcceptsAsync(reader, token);

        // Then
        Assert.True(accepted, $"the {reader} reader refused the es256 control");
    }

    [UnitTheory]
    [MemberData(nameof(ReadersAndAlgSpellings))]
    public async Task GivenAnAlgInAnotherCaseOrWithASpace_WhenAnyReaderReadsIt_ThenItIsRefused(string reader, string alg)
    {
        // Given — a token otherwise valid for this reader, its alg written as given
        var readers = NewReaders();
        var token = UnderAlg(readers, reader, alg);
        Assert.Equal(alg, new JsonWebToken(token).Alg);

        // When
        var refused = await readers.RefusesAsync(reader, token);

        // Then
        Assert.True(refused, $"the {reader} reader accepted alg \"{alg}\"");
    }

    [UnitTheory]
    [MemberData(nameof(ReadersAlone))]
    public async Task GivenTheAlgWrittenES256_WhenEachReaderReadsTheSameConstruction_ThenItIsAccepted(string reader)
    {
        // Given — the theory above's construction with the alg as Heimdall writes it, so it is only the
        // spelling that a refusal there turns on
        var readers = NewReaders();
        var token = UnderAlg(readers, reader, "ES256");

        // When
        var accepted = await readers.AcceptsAsync(reader, token);

        // Then
        Assert.True(accepted, $"the {reader} reader refused the hand-built es256 control");
    }
}
