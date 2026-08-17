using System.Net;
using ArturRios.Configuration.Enums;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Query.Output;
using ArturRios.Heimdall.WebApi.Security;
using ArturRios.Heimdall.WebApi.Tests.Support;
using ArturRios.Jwt;
using ArturRios.Output;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Http;
using ArturRios.Util.Test.Functional;

namespace ArturRios.Heimdall.WebApi.Tests;

// Functional tests for signing-key rotation (Threat Model TH-22).
//
// Replacing the signing secret used to be a cutover: the moment a new one took effect, every token in
// flight was refused. That made a leaked key and a routine replacement cost the same outage, and it
// is why the secret was in practice never rotated at all.
//
// HEIMDALL_AUTH_TOKEN_SECRET_PREVIOUS is the other half of a rotation. Both secrets are handed to
// JwtConfiguration.Keys, so a token signed with either is accepted while new ones are signed with the
// current secret. PostgresFixture publishes both before the host starts, because the API reads its
// key material once at start-up.
[Collection(nameof(FunctionalCollection))]
public class SigningKeyRotationTests() : WebApiTest<Program>(EnvironmentType.Local)
{
    /// <summary>
    ///     Mints a token the way <see cref="TestTokens" /> does, but signed with a chosen secret, so a
    ///     test can produce the token a caller was holding before a rotation.
    /// </summary>
    private static string TokenSignedWith(string secret, Guid personId, int role)
    {
        var configuration = new JwtConfiguration(
            3600,
            Environment.GetEnvironmentVariable("HEIMDALL_AUTH_TOKEN_ISSUER") ?? string.Empty,
            Environment.GetEnvironmentVariable("HEIMDALL_AUTH_TOKEN_AUDIENCE") ?? string.Empty,
            secret,
            new IdentityUserMapper().ToClaims(new IdentityUser(personId, role)));

        return new JwtHandler().CreateToken(configuration);
    }

    private Task<HttpOutput<DataOutput<PersonOutput?>?>> ReadSelfAsync(Guid personId) =>
        Gateway.GetAsync<DataOutput<PersonOutput?>>($"/api/persons/{personId}");

    [FunctionalFact]
    public async Task GivenATokenSignedWithTheRetiredSecret_WhenCallingAnEndpoint_ThenItIsStillAccepted()
    {
        // The point of the whole feature: this is the token a caller was holding when the rotation
        // happened, and they must not be signed out by it.
        var personId = PostgresFixture.StandInPersonIds[Roles.SystemAdmin];

        Authorize(TokenSignedWith(PostgresFixture.PreviousTokenSecret, personId, (int)Roles.SystemAdmin));

        var response = await ReadSelfAsync(personId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenATokenSignedWithTheCurrentSecret_WhenCallingAnEndpoint_ThenItIsAccepted()
    {
        // The control. Accepting the retired key must not have come at the cost of the current one —
        // and the rest of the suite, which signs every token with the current secret, is the wider
        // version of this assertion.
        var personId = PostgresFixture.StandInPersonIds[Roles.SystemAdmin];

        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        Assert.Equal(HttpStatusCode.OK, (await ReadSelfAsync(personId)).StatusCode);
    }

    [FunctionalFact]
    public async Task GivenATokenSignedWithASecretThatWasNeverConfigured_WhenCallingAnEndpoint_ThenItIsRefused()
    {
        // Accepting two keys is not accepting any key. A withdrawn secret — or one that was never
        // ours — buys nothing, which is what makes withdrawing a leaked key an effective revocation.
        var personId = PostgresFixture.StandInPersonIds[Roles.SystemAdmin];

        Authorize(TokenSignedWith("a-secret-this-deployment-never-accepted", personId, (int)Roles.SystemAdmin));

        Assert.Equal(HttpStatusCode.Unauthorized, (await ReadSelfAsync(personId)).StatusCode);
    }
}
