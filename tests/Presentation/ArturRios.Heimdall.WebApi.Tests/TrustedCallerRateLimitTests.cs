using System.Net;
using System.Net.Http.Json;
using ArturRios.Configuration.Enums;
using ArturRios.Heimdall.WebApi.Security;
using ArturRios.Heimdall.WebApi.Tests.Support;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Functional;

namespace ArturRios.Heimdall.WebApi.Tests;

// Functional tests for the trusted caller key against the real rate limiter. The in-memory test
// server carries no remote address, so without a key every request is one caller — the position
// every end user of a client system is in when it reaches Heimdall from its own address. With the
// fixture's key, the client system names each user's address and each gets a budget of its own.
//
// verify-email is an AuthAnonymous endpoint, and a made-up token costs one indexed lookup, not an
// Argon2id derivation, so eleven requests are cheap.
[Collection(nameof(FunctionalCollection))]
public class TrustedCallerRateLimitTests() : WebApiTest<Program>(EnvironmentType.Local)
{
    private const string Route = "/api/auth/verify-email";
    private const int PermitLimit = 10;

    private async Task<HttpStatusCode> PostAsync(string? key, string? clientAddress)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Route)
        {
            Content = JsonContent.Create(new { token = $"token-{Guid.NewGuid():N}" })
        };

        if (key is not null)
        {
            request.Headers.Add(AuthRateLimitPartition.CallerKeyHeader, key);
        }

        if (clientAddress is not null)
        {
            request.Headers.Add(AuthRateLimitPartition.ClientAddressHeader, clientAddress);
        }

        using var response = await Gateway.Client.SendAsync(request);

        return response.StatusCode;
    }

    private async Task ExhaustAsync(string? key, string clientAddress)
    {
        for (var attempt = 1; attempt <= PermitLimit; attempt++)
        {
            Assert.NotEqual(HttpStatusCode.TooManyRequests, await PostAsync(key, clientAddress));
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, await PostAsync(key, clientAddress));
    }

    [FunctionalFact]
    public async Task GivenAValidKey_WhenTwoClientAddressesSendRequests_ThenEachHasItsOwnBudget()
    {
        await ExhaustAsync(PostgresFixture.TrustedCallerKey, "203.0.113.7");

        // Address A is spent; address B, named by the same trusted caller, is a different user.
        Assert.NotEqual(
            HttpStatusCode.TooManyRequests, await PostAsync(PostgresFixture.TrustedCallerKey, "203.0.113.8"));
    }

    [FunctionalFact]
    public async Task GivenANamedAddressIsExhausted_WhenTheConnectionIsNotThatAddress_ThenTheDirectBucketIsUntouched()
    {
        // The functional host has no remote address, so a request with no headers is the direct
        // connection (partition "unknown"); the same-address direct case is pinned by the unit tests
        // in AuthRateLimitPartitionTests, where the connection address can be set.
        await ExhaustAsync(PostgresFixture.TrustedCallerKey, "203.0.113.7");

        Assert.NotEqual(HttpStatusCode.TooManyRequests, await PostAsync(key: null, clientAddress: null));
    }

    [FunctionalFact]
    public async Task GivenOneIPv6Slash56SpreadOverItsSlash64s_WhenItsBudgetIsSpent_ThenItIsRefusedAndAnotherSlash56IsServed()
    {
        // Given ten requests, each from a different /64 of one /56 — what one subscriber handed a /56
        // can send without ever reusing a /64
        for (var subnet = 0; subnet < PermitLimit; subnet++)
        {
            Assert.NotEqual(
                HttpStatusCode.TooManyRequests,
                await PostAsync(PostgresFixture.TrustedCallerKey, $"2001:db8:5:7{subnet:x2}::1"));
        }

        // When an eleventh arrives from yet another /64 of it, and a request from the next /56
        var flooding = await PostAsync(PostgresFixture.TrustedCallerKey, "2001:db8:5:7ff::1");
        var neighbour = await PostAsync(PostgresFixture.TrustedCallerKey, "2001:db8:5:800::1");

        // Then the /56 is one caller, and its neighbour is not charged for it
        Assert.Equal(HttpStatusCode.TooManyRequests, flooding);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, neighbour);
    }

    [FunctionalFact]
    public async Task GivenNoKey_WhenDifferentClientAddressesAreNamed_ThenTheyShareOneBudget()
    {
        await ExhaustAsync(key: null, "203.0.113.7");

        // The address header alone proves nothing: it is ignored and the caller stays in the bucket.
        Assert.Equal(HttpStatusCode.TooManyRequests, await PostAsync(key: null, "203.0.113.8"));
    }

    [FunctionalFact]
    public async Task GivenAWrongKey_WhenDifferentClientAddressesAreNamed_ThenTheyShareOneBudgetAndNothingErrors()
    {
        const string wrongKey = "wrong-caller-key-0123456789abcdefghijk";

        await ExhaustAsync(wrongKey, "203.0.113.7");

        Assert.Equal(HttpStatusCode.TooManyRequests, await PostAsync(wrongKey, "203.0.113.8"));
    }

    [FunctionalFact]
    public async Task GivenAValidKey_WhenTheAddressIsGarbage_ThenTheRequestIsServedFromTheConnectionBucket()
    {
        // The headers never error a request: a bad address is just not believed.
        var status = await PostAsync(PostgresFixture.TrustedCallerKey, "not-an-address");

        Assert.NotEqual(HttpStatusCode.TooManyRequests, status);
        Assert.NotEqual(HttpStatusCode.InternalServerError, status);
    }
}
