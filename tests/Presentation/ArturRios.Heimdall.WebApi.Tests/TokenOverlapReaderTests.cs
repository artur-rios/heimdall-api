using ArturRios.Heimdall.WebApi.Security;
using ArturRios.Heimdall.WebApi.Tests.Support;
using ArturRios.Util.Test.Attributes;
using Microsoft.Extensions.Logging;

namespace ArturRios.Heimdall.WebApi.Tests;

// Unit tests for the overlap at each of Heimdall's three readers (spec decision 5; rulings R4, R5):
// while the legacy secret is configured, an HS256 token Heimdall issued before this process started is
// still read as what it is, and each acceptance writes one Information line, which is what tells an
// operator when the overlap can end. Once the secret is removed, HS256 is refused everywhere, and an
// ES256 token is accepted either way, with nothing logged. Each reader bounds a legacy token by its own
// kind's lifetime.
public class TokenOverlapReaderTests
{
    // Generated for these tests (openssl rand -hex 20); no environment signs with them.
    private const string Secret = "unit-overlap-legacy-4d6e8f0a1b3c5d7e9f2a4b6c8d0e1f3a5b7c9d2e";

    public static TheoryData<string> Readers() =>
    [
        LegacyTokenValidator.BearerReader, LegacyTokenValidator.ChallengeReader, LegacyTokenValidator.ApplicationReader
    ];

    private static UnitTokenReaders DuringTheOverlap() => new(TokenSigningSecrets.Parse(Secret, null));

    private static UnitTokenReaders AfterTheOverlap() => new(TokenSigningSecrets.Parse(null, null));

    private static async Task AssertLegacyTokenIsReadAndLoggedAsync(string reader)
    {
        // Given
        var readers = DuringTheOverlap();
        var token = readers.TokenFor(reader, UnitSigningKeys.Hs256(Secret));

        // When
        var accepted = await readers.AcceptsAsync(reader, token);

        // Then — read as what it is, and recorded once, naming the reader and no identity
        Assert.True(accepted);
        Assert.Equal([(LogLevel.Information, UnitTokenReaders.AcceptedLine(reader))], readers.Log.Entries);
    }

    private static async Task AssertLegacyTokenIsRefusedAsync(string reader)
    {
        // Given — the same token, once HEIMDALL_AUTH_TOKEN_SECRET is removed
        var readers = AfterTheOverlap();
        var token = readers.TokenFor(reader, UnitSigningKeys.Hs256(Secret));

        // When
        var refused = await readers.RefusesAsync(reader, token);

        // Then
        Assert.True(refused);
        Assert.Empty(readers.Log.Entries);
    }

    [UnitFact]
    public Task GivenTheOverlap_WhenALegacyBearerTokenArrives_ThenItsPersonIsResolved() =>
        AssertLegacyTokenIsReadAndLoggedAsync(LegacyTokenValidator.BearerReader);

    [UnitFact]
    public Task GivenTheOverlapHasEnded_WhenALegacyBearerTokenArrives_ThenItIsRefused() =>
        AssertLegacyTokenIsRefusedAsync(LegacyTokenValidator.BearerReader);

    [UnitFact]
    public Task GivenTheOverlap_WhenALegacyChallengeTokenArrives_ThenItIsReadAsAChallenge() =>
        AssertLegacyTokenIsReadAndLoggedAsync(LegacyTokenValidator.ChallengeReader);

    [UnitFact]
    public Task GivenTheOverlapHasEnded_WhenALegacyChallengeTokenArrives_ThenItIsRefused() =>
        AssertLegacyTokenIsRefusedAsync(LegacyTokenValidator.ChallengeReader);

    [UnitFact]
    public Task GivenTheOverlap_WhenALegacyApplicationTokenArrives_ThenItIsReadAsAnApplication() =>
        AssertLegacyTokenIsReadAndLoggedAsync(LegacyTokenValidator.ApplicationReader);

    [UnitFact]
    public Task GivenTheOverlapHasEnded_WhenALegacyApplicationTokenArrives_ThenItIsRefused() =>
        AssertLegacyTokenIsRefusedAsync(LegacyTokenValidator.ApplicationReader);

    [UnitTheory]
    [MemberData(nameof(Readers))]
    public async Task GivenAnEs256Token_WhenTheOverlapHasEnded_ThenEveryReaderStillAcceptsIt(string reader)
    {
        // Given
        var readers = AfterTheOverlap();
        var token = readers.TokenFor(reader, UnitSigningKeys.Es256(readers.Keys.Current));

        // When
        var accepted = await readers.AcceptsAsync(reader, token);

        // Then
        Assert.True(accepted);
        Assert.Empty(readers.Log.Entries);
    }

    [UnitTheory]
    [MemberData(nameof(Readers))]
    public async Task GivenAnEs256Token_WhenReadDuringTheOverlap_ThenItIsAcceptedAndNothingIsLogged(string reader)
    {
        // Given — R5: only a legacy acceptance is logged
        var readers = DuringTheOverlap();
        var token = readers.TokenFor(reader, UnitSigningKeys.Es256(readers.Keys.Current));

        // When
        var accepted = await readers.AcceptsAsync(reader, token);

        // Then
        Assert.True(accepted);
        Assert.Empty(readers.Log.Entries);
    }

    [UnitTheory]
    [MemberData(nameof(Readers))]
    public async Task GivenALegacyTokenIssuedAfterTheProcessStarted_WhenEachReaderReadsIt_ThenItIsRefused(string reader)
    {
        // Given — R4: correctly signed with the legacy secret, but issued a minute after this process
        // started, which signs ES256 alone, so Heimdall cannot have issued it
        var readers = DuringTheOverlap();
        var token = readers.TokenFor(reader, UnitSigningKeys.Hs256(Secret), issuedAt: TimeSpan.FromMinutes(1));

        // When
        var refused = await readers.RefusesAsync(reader, token);

        // Then
        Assert.True(refused);
        Assert.Empty(readers.Log.Entries);
    }

    [UnitTheory]
    [InlineData(LegacyTokenValidator.BearerReader, 60, true)]
    [InlineData(LegacyTokenValidator.BearerReader, 61, false)]
    [InlineData(LegacyTokenValidator.ChallengeReader, 10, true)]
    [InlineData(LegacyTokenValidator.ChallengeReader, 11, false)]
    [InlineData(LegacyTokenValidator.ApplicationReader, 20, true)]
    [InlineData(LegacyTokenValidator.ApplicationReader, 21, false)]
    public async Task GivenALegacyTokenExpiringAfterTheStart_WhenEachReaderReadsIt_ThenItsOwnKindsLifetimeBoundsIt(
        string reader, int minutesAfterStart, bool accepted)
    {
        // Given — R4: a token issued before the start expires within its kind's lifetime of it: an
        // hour for a person token, ten minutes for a challenge, twenty for this application lifetime
        var readers = DuringTheOverlap();
        var token = readers.TokenFor(
            reader, UnitSigningKeys.Hs256(Secret), expires: TimeSpan.FromMinutes(minutesAfterStart));

        // When
        var read = accepted
            ? await readers.AcceptsAsync(reader, token)
            : await readers.RefusesAsync(reader, token);

        // Then
        Assert.True(read, $"the {reader} reader {(accepted ? "refused" : "accepted")} it");
    }
}
