using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ArturRios.Configuration.Enums;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Heimdall.WebApi.Security;
using ArturRios.Heimdall.WebApi.Tests.Support;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Functional;

namespace ArturRios.Heimdall.WebApi.Tests;

// Functional test for what a rate-limited caller is told (TH-32, NFR): a 429 carries Retry-After and
// the API's usual error envelope, not an empty body. Uses verify-email with a made-up token, as
// TrustedCallerRateLimitTests does, so eleven requests cost eleven indexed lookups, not eleven
// Argon2id derivations. A unique client address under the fixture's trusted key gives the test a
// bucket of its own.
[Collection(nameof(FunctionalCollection))]
public class RateLimitRejectionTests() : WebApiTest<Program>(EnvironmentType.Local)
{
    private const string Route = "/api/auth/verify-email";
    private const int PermitLimit = 10;

    private async Task<HttpResponseMessage> PostAsync(string clientAddress)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Route)
        {
            Content = JsonContent.Create(new { token = $"token-{Guid.NewGuid():N}" })
        };

        request.Headers.Add(AuthRateLimitPartition.CallerKeyHeader, PostgresFixture.TrustedCallerKey);
        request.Headers.Add(AuthRateLimitPartition.ClientAddressHeader, clientAddress);

        return await Gateway.Client.SendAsync(request);
    }

    [FunctionalFact]
    public async Task GivenTheBudgetIsSpent_WhenAnotherRequestArrives_ThenItIs429WithRetryAfterAndTheEnvelope()
    {
        // Given
        const string address = "203.0.113.50";

        for (var attempt = 1; attempt <= PermitLimit; attempt++)
        {
            using var allowed = await PostAsync(address);
            Assert.NotEqual(HttpStatusCode.TooManyRequests, allowed.StatusCode);
        }

        // When
        using var response = await PostAsync(address);
        var body = await response.Content.ReadAsStringAsync();

        // Then
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);

        Assert.NotNull(response.Headers.RetryAfter);
        Assert.NotNull(response.Headers.RetryAfter.Delta);
        Assert.InRange(response.Headers.RetryAfter.Delta.Value.TotalSeconds, 1, 60);

        using var document = JsonDocument.Parse(body);
        var errors = document.RootElement.GetProperty("errors").EnumerateArray().Select(e => e.GetString());

        Assert.Contains(RateLimitMessages.TooManyRequests, errors);
    }
}
