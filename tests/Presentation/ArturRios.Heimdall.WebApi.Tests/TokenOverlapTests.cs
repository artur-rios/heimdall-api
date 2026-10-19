using System.Net;
using System.Net.Http.Headers;
using ArturRios.Configuration.Enums;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Query.Output;
using ArturRios.Heimdall.WebApi.Security;
using ArturRios.Heimdall.WebApi.Tests.Support;
using ArturRios.Output;
using ArturRios.Util.Http;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Functional;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ArturRios.Heimdall.WebApi.Tests;

// Functional tests for the overlap after the switch to ES256 (spec decision 5). Heimdall signs every
// token ES256, but while HEIMDALL_AUTH_TOKEN_SECRET is set it still accepts the HS256 tokens it issued
// before it started — signed with that secret or the previous one — so nobody is signed out by the
// deploy. Removing the secret ends the overlap: HS256 is refused, and ES256 works as before.
[Collection(nameof(FunctionalCollection))]
public class TokenOverlapTests() : WebApiTest<Program>(EnvironmentType.Local)
{
    private static readonly Guid SystemAdmin = PostgresFixture.StandInPersonIds[Roles.SystemAdmin];

    private Task<HttpOutput<DataOutput<PersonOutput?>?>> ReadSelfAsync() =>
        Gateway.GetAsync<DataOutput<PersonOutput?>>($"/api/persons/{SystemAdmin}");

    [FunctionalFact]
    public async Task GivenALegacyHs256Token_WhenCallingAnEndpointDuringTheOverlap_ThenItIsAccepted()
    {
        // Given — the token a caller was holding when 3.0.0 was deployed
        Authorize(TestTokens.Legacy(SystemAdmin, (int)Roles.SystemAdmin));

        // When
        var response = await ReadSelfAsync();

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenALegacyHs256TokenFromThePreviousSecret_WhenCallingAnEndpointDuringTheOverlap_ThenItIsAccepted()
    {
        // Given — signed before a secret rotation that was still in progress at the deploy (TH-22)
        Authorize(TestTokens.Legacy(SystemAdmin, (int)Roles.SystemAdmin, PostgresFixture.PreviousTokenSecret));

        // When
        var response = await ReadSelfAsync();

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenALegacyHs256TokenIssuedAfterTheHostStarted_WhenCallingAnEndpoint_ThenItIs401()
    {
        // Given — R4: correctly signed with the legacy secret, but issued after this host started.
        // The host signs ES256 alone, so it cannot have issued it: whoever else holds the secret did.
        // The host is already running (the gateway's client started it), and the issue time is two
        // seconds ahead, so even in whole seconds it falls after the start.
        Authorize(TestTokens.Legacy(
            SystemAdmin, (int)Roles.SystemAdmin, issuedAt: DateTime.UtcNow.AddSeconds(2)));

        // When
        var response = await ReadSelfAsync();

        // Then
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenTheOverlapHasEnded_WhenALegacyHs256TokenIsUsed_ThenItIs401AndAnEs256TokenStillWorks()
    {
        // Given — a host started without the legacy secrets: the key material is read once, at
        // start-up, so this test starts its own
        var savedEnvironment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        var savedSecret = Environment.GetEnvironmentVariable(TokenSigningSecrets.SecretVariable);
        var savedPrevious = Environment.GetEnvironmentVariable(TokenSigningSecrets.PreviousSecretVariable);
        HttpStatusCode legacy;
        HttpStatusCode es256;

        try
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "local");
            Environment.SetEnvironmentVariable(TokenSigningSecrets.SecretVariable, null);
            Environment.SetEnvironmentVariable(TokenSigningSecrets.PreviousSecretVariable, null);

            await using var factory = new WebApplicationFactory<Program>()
                .WithWebHostBuilder(builder => builder.UseEnvironment("Local"));

            using var client = factory.CreateClient();

            // When
            legacy = await StatusWithAsync(client, TestTokens.Legacy(SystemAdmin, (int)Roles.SystemAdmin));
            es256 = await StatusWithAsync(client, TestTokens.ForRole((int)Roles.SystemAdmin));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", savedEnvironment);
            Environment.SetEnvironmentVariable(TokenSigningSecrets.SecretVariable, savedSecret);
            Environment.SetEnvironmentVariable(TokenSigningSecrets.PreviousSecretVariable, savedPrevious);
        }

        // Then
        Assert.Equal(HttpStatusCode.Unauthorized, legacy);
        Assert.Equal(HttpStatusCode.OK, es256);
    }

    private static async Task<HttpStatusCode> StatusWithAsync(HttpClient client, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/persons/{SystemAdmin}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await client.SendAsync(request);

        return response.StatusCode;
    }
}
