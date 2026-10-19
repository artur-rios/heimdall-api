using System.Net;
using ArturRios.Configuration.Enums;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Query.Output;
using ArturRios.Heimdall.WebApi.Security;
using ArturRios.Heimdall.WebApi.Tests.Support;
using ArturRios.Output;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Http;
using ArturRios.Util.Test.Functional;
using Microsoft.IdentityModel.JsonWebTokens;

namespace ArturRios.Heimdall.WebApi.Tests;

// Functional tests for signing-key rotation (Threat Model TH-22).
//
// Replacing the signing key used to be a cutover: the moment a new one took effect, every token in
// flight was refused. That made a leaked key and a routine replacement cost the same outage, and it
// is why the key was in practice never rotated at all.
//
// HEIMDALL_AUTH_SIGNING_KEY_PREVIOUS is the other half of a rotation. Its public half is published
// beside the current key's and verifies the tokens it signed, while new ones are signed with the
// current key alone; each token names its key by thumbprint in its kid. PostgresFixture publishes both
// before the host starts, because the API reads its key material once at start-up.
[Collection(nameof(FunctionalCollection))]
public class SigningKeyRotationTests() : WebApiTest<Program>(EnvironmentType.Local)
{
    private static readonly Guid SystemAdmin = PostgresFixture.StandInPersonIds[Roles.SystemAdmin];

    private static IdentityUser SystemAdminIdentity() =>
        new(SystemAdmin, (int)Roles.SystemAdmin) { SecurityStamp = TestTokens.StampOf(SystemAdmin) };

    private Task<HttpOutput<DataOutput<PersonOutput?>?>> ReadSelfAsync() =>
        Gateway.GetAsync<DataOutput<PersonOutput?>>($"/api/persons/{SystemAdmin}");

    [FunctionalFact]
    public async Task GivenATokenSignedWithThePreviousSigningKey_WhenCallingAnEndpoint_ThenItIsStillAccepted()
    {
        // Given — the point of the whole feature: this is the token a caller was holding when the
        // rotation happened, and they must not be signed out by it
        Authorize(TestTokens.SignedWith(PostgresFixture.PreviousSigningKey, SystemAdminIdentity()));

        // When
        var response = await ReadSelfAsync();

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenATokenSignedWithTheCurrentSigningKey_WhenCallingAnEndpoint_ThenItIsAccepted()
    {
        // Given — the control. Accepting the previous key must not have come at the cost of the
        // current one, and the rest of the suite, which signs every token with the current key, is the
        // wider version of this assertion
        Authorize(TestTokens.SignedWith(PostgresFixture.SigningKey, SystemAdminIdentity()));

        // When
        var response = await ReadSelfAsync();

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenATokenSignedWithAKeyThatWasNeverConfigured_WhenCallingAnEndpoint_ThenItIsRefused()
    {
        // Given — accepting two keys is not accepting any key. A withdrawn key, or one that was never
        // ours, buys nothing, which is what makes withdrawing a leaked key an effective revocation
        Authorize(TestTokens.SignedWith(UnitSigningKeys.Create().Key, SystemAdminIdentity()));

        // When
        var response = await ReadSelfAsync();

        // Then
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}

// The rotation at the challenge reader, which reads its token outside the bearer path: a UC-38
// challenge issued just before the rotation is still a challenge after it.
public class SigningKeyRotationReaderTests
{
    [UnitFact]
    public async Task GivenAChallengeTokenFromThePreviousSigningKey_WhenRead_ThenItIsAChallenge()
    {
        // Given — a rotation in progress: a new current key, and the previous one's public half
        var previous = UnitSigningKeys.Create();
        var keys = TokenSigningKeys.Parse(UnitSigningKeys.Create().EnvValue, previous.EnvValue);
        var readers = new UnitTokenReaders([], keys);
        var challenge = readers.TokenFor(LegacyTokenValidator.ChallengeReader, UnitSigningKeys.Es256(previous.Key));
        Assert.Equal(keys.Previous!.Id, new JsonWebToken(challenge).Kid);

        // When
        var accepted = await readers.AcceptsAsync(LegacyTokenValidator.ChallengeReader, challenge);

        // Then
        Assert.True(accepted);
    }
}
