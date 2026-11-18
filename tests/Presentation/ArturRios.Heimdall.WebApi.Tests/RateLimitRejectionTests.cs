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

    // The published document's 429 is what a client generated from it is told to expect: the media
    // type the rejection is written with, and the envelope's members, nothing more and nothing less.
    [FunctionalFact]
    public async Task GivenTheBudgetIsSpent_WhenRejected_ThenTheBodyIsTheDocumented429()
    {
        // Given
        const string address = "203.0.113.51";

        for (var attempt = 1; attempt <= PermitLimit; attempt++)
        {
            using var allowed = await PostAsync(address);
            Assert.NotEqual(HttpStatusCode.TooManyRequests, allowed.StatusCode);
        }

        using var openApi = LoadDocument();
        var documented = openApi.RootElement.GetProperty("paths").GetProperty(Route).GetProperty("post")
            .GetProperty("responses").GetProperty("429").GetProperty("content");
        // The API's own rejection; Traefik's text/plain beside it is the proxy's, which this host has not.
        var mediaType = documented.EnumerateObject().Single(media => media.Name == "application/json");
        var schemaName = mediaType.Value.GetProperty("schema").GetProperty("$ref").GetString()!.Split('/')[^1];
        var properties = openApi.RootElement.GetProperty("components").GetProperty("schemas").GetProperty(schemaName)
            .GetProperty("properties").EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal);

        // When
        using var response = await PostAsync(address);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        // Then
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal(mediaType.Name, response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(properties, body.RootElement.EnumerateObject().Select(member => member.Name).Order(StringComparer.Ordinal));
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("data").ValueKind);
    }

    private static JsonDocument LoadDocument() =>
        JsonDocument.Parse(File.ReadAllText(
            Environment.GetEnvironmentVariable("HEIMDALL_OPENAPI_DOCUMENT")
            ?? Path.Combine(Repository.Root, "docs", "openapi", "heimdall.json")));
}
