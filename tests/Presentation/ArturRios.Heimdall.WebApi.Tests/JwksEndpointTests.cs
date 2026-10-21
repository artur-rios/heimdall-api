using System.Net;
using System.Text.Json;
using ArturRios.Configuration.Enums;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.WebApi.Security;
using ArturRios.Heimdall.WebApi.Tests.Support;
using ArturRios.Jwt;
using ArturRios.Output;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Functional;
using Microsoft.IdentityModel.JsonWebTokens;

namespace ArturRios.Heimdall.WebApi.Tests;

// Functional tests for GET /.well-known/jwks.json: the raw RFC 7517 set a consumer verifies Heimdall's
// ES256 tokens with, served to anyone, whatever bearer token the request happens to carry.
[Collection(nameof(FunctionalCollection))]
public class JwksEndpointTests() : WebApiTest<Program>(EnvironmentType.Local)
{
    private const string Route = "/.well-known/jwks.json";

    private static string Published =>
        JwksDocument.From([PostgresFixture.SigningKey, PostgresFixture.PreviousSigningKey.ToPublicKey()]);

    // The bearer token travels on this request alone, never on the client's default headers.
    private async Task<(HttpResponseMessage Response, string Body)> GetAsync(string? bearer = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Route);

        if (bearer is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {bearer}");
        }

        var response = await Gateway.Client.SendAsync(request);

        return (response, await response.Content.ReadAsStringAsync());
    }

    [FunctionalFact]
    public async Task GivenNoToken_WhenTheKeySetIsRequested_ThenItIsExactlyThePublishedSet()
    {
        // Given / When
        var (response, body) = await GetAsync();

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Published, body);
        Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("public, max-age=300", response.Headers.CacheControl!.ToString());
    }

    [FunctionalFact]
    public async Task GivenAPreviousKey_WhenTheKeySetIsRead_ThenBothKeysAreListedCurrentFirst()
    {
        // Given / When
        var (_, body) = await GetAsync();

        // Then
        using var document = JsonDocument.Parse(body);
        var keys = document.RootElement.GetProperty("keys").EnumerateArray().ToList();

        Assert.Equal(
            [PostgresFixture.SigningKey.Id, PostgresFixture.PreviousSigningKey.Id],
            keys.Select(key => key.GetProperty("kid").GetString()));

        foreach (var key in keys)
        {
            Assert.Equal(["kty", "crv", "x", "y", "kid", "use", "alg"], key.EnumerateObject().Select(p => p.Name));
            Assert.Equal("EC", key.GetProperty("kty").GetString());
            Assert.Equal("P-256", key.GetProperty("crv").GetString());
            Assert.Equal("sig", key.GetProperty("use").GetString());
            Assert.Equal("ES256", key.GetProperty("alg").GetString());
            Assert.False(key.TryGetProperty("d", out _));
        }
    }

    public static TheoryData<string> BearerKinds => new()
    {
        "garbage",
        "expired",
        "mfa-pending",
        "subject-rights"
    };

    [FunctionalTheory]
    [MemberData(nameof(BearerKinds))]
    public async Task GivenAnyBearerToken_WhenTheKeySetIsRequested_ThenItIsServed(string kind)
    {
        // Given
        var token = kind switch
        {
            "garbage" => "a.b.c",
            "expired" => TestTokens.ExpiredMfaPending(Guid.NewGuid(), (int)Roles.User),
            "mfa-pending" => TestTokens.ForMfaPending(PostgresFixture.StandInPersonIds[Roles.User], (int)Roles.User),
            _ => TestTokens.WithAudience(
                new IdentityUser(PostgresFixture.StandInPersonIds[Roles.User], (int)Roles.User)
                {
                    SubjectRightsOnly = true
                },
                TestTokens.Audiences.SubjectRights)
        };

        // When
        var (response, body) = await GetAsync(token);

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Published, body);
    }

    [FunctionalFact]
    public async Task GivenATokenHeimdallIssued_WhenAFortunaStyleReaderVerifiesItWithThePublishedSet_ThenItIsValid()
    {
        // Given — a sign-in
        var login = await Gateway.PostAsync<DataOutput<LoginCommandOutput?>>(
            "/api/auth/login",
            new LoginCommand { Email = PostgresFixture.MasterUserEmail, Password = PostgresFixture.MasterUserPassword });
        var token = login.Body!.Data!.Token!;

        // When — the set is read as a consumer reads it
        var (_, body) = await GetAsync();
        var keys = JwksDocument.Parse(body);

        // Then
        Assert.Contains(keys, key => key.Id == new JsonWebToken(token).Kid);
        Assert.True(await new JwtHandler().IsTokenValidAsync(token, keys));
    }
}
