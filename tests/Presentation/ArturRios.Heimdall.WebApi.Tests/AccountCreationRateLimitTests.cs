using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ArturRios.Configuration.Enums;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Heimdall.WebApi.Security;
using ArturRios.Heimdall.WebApi.Tests.Support;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Functional;

namespace ArturRios.Heimdall.WebApi.Tests;

// Tests for the rate limit on the endpoints that create a person with a password (Threat Model TH-03).
// Each derives an Argon2id hash through the process-wide gate, and each needs nothing but an
// administrator's token, so without a bound one account — from one address — could keep the gate full
// and starve every sign-in. The limit is per acting person, sized for real administration.
//
// The limit is per acting person and per address too; only an administrator's person access token
// spends the address bound. The functional tests spend the budget with malformed bodies: the
// limiter counts a request before MVC sees it, so a 400 costs a slot as a created person does,
// without thirty derivations.
[Collection(nameof(FunctionalCollection))]
public class AccountCreationRateLimitTests(PostgresFixture db) : WebApiTest<Program>(EnvironmentType.Local)
{
    private async Task<Guid> SeedSystemAdminAsync()
    {
        await using var context = db.CreateContext();
        var admin = new Person
        {
            PublicId = Guid.NewGuid(), Name = "Creating Admin", Email = $"creator-{Guid.NewGuid():N}@test.local",
            PasswordHash = [1], Salt = [2], RoleId = (long)Roles.SystemAdmin, EmailVerified = true
        };
        context.Persons.Add(admin);
        await context.SaveChangesAsync();

        return admin.PublicId;
    }

    private async Task<Guid> SeedUserAsync()
    {
        await using var context = db.CreateContext();
        var user = new Person
        {
            PublicId = Guid.NewGuid(), Name = "Plain User", Email = $"user-{Guid.NewGuid():N}@test.local",
            PasswordHash = [1], Salt = [2], RoleId = (long)Roles.User, EmailVerified = true
        };
        context.Persons.Add(user);
        await context.SaveChangesAsync();

        return user.PublicId;
    }

    private async Task<string> SubjectRightsTokenAsync()
    {
        var admin = await SeedSystemAdminAsync();

        return TestTokens.WithAudience(
            new IdentityUser(admin, (int)Roles.SystemAdmin)
            {
                SubjectRightsOnly = true,
                SecurityStamp = TestTokens.StampOf(admin)
            },
            TestTokens.Audiences.SubjectRights);
    }

    private Task<HttpResponseMessage> CreateAsync(Guid? actor, string clientAddress) =>
        CreateWithTokenAsync(actor is { } id ? TestTokens.For(id, (int)Roles.SystemAdmin) : null, clientAddress);

    private async Task<HttpResponseMessage> CreateWithTokenAsync(string? token, string clientAddress)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/persons")
        {
            Content = JsonContent.Create(new { })
        };

        if (token is not null)
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        }

        // A trusted caller's named address, so each test chooses the address bucket it is counted in.
        request.Headers.Add(AuthRateLimitPartition.CallerKeyHeader, PostgresFixture.TrustedCallerKey);
        request.Headers.Add(AuthRateLimitPartition.ClientAddressHeader, clientAddress);

        return await Gateway.Client.SendAsync(request);
    }

    private async Task SpendAsync(Guid? actor, string clientAddress, int requests)
    {
        for (var attempt = 1; attempt <= requests; attempt++)
        {
            using var allowed = await CreateAsync(actor, clientAddress);
            Assert.NotEqual(HttpStatusCode.TooManyRequests, allowed.StatusCode);
        }
    }

    private static async Task<string?> FirstErrorAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        return document.RootElement.TryGetProperty("errors", out var errors)
            ? errors.EnumerateArray().Select(error => error.GetString()).FirstOrDefault()
            : null;
    }

    [FunctionalFact]
    public async Task GivenOneActorSpentTheMinutesBudget_WhenTheyCreateAgainFromAnotherAddress_ThenTooManyRequests()
    {
        // Given an actor who spent thirty from one address
        var actor = await SeedSystemAdminAsync();
        await SpendAsync(actor, "203.0.113.70", Startup.AccountCreationPermitLimit);

        // When they move to an address of their own
        using var limited = await CreateAsync(actor, "203.0.113.71");

        // Then — the actor's own bound still holds
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.NotNull(limited.Headers.RetryAfter);
        Assert.Equal(RateLimitMessages.TooManyRequests, await FirstErrorAsync(limited));
    }

    [FunctionalFact]
    public async Task GivenOneAddressSpentTheMinutesBudget_WhenAnotherActorCreatesFromIt_ThenTooManyRequestsAndAnotherAddressIsServed()
    {
        // Given — the address is bounded too: one actor spends thirty from an address
        var first = await SeedSystemAdminAsync();
        var second = await SeedSystemAdminAsync();
        await SpendAsync(first, "203.0.113.72", Startup.AccountCreationPermitLimit);

        // When a fresh actor creates from the same address, and from another
        using var sameAddress = await CreateAsync(second, "203.0.113.72");
        using var otherAddress = await CreateAsync(second, "203.0.113.73");

        // Then
        Assert.Equal(HttpStatusCode.TooManyRequests, sameAddress.StatusCode);
        Assert.Equal(RateLimitMessages.TooManyRequests, await FirstErrorAsync(sameAddress));
        Assert.NotEqual(HttpStatusCode.TooManyRequests, otherAddress.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenAnonymousRequestsSpentAnAddressesMinute_WhenAnAdministratorCreatesFromIt_ThenTheyAreServed()
    {
        // Given thirty requests without a token from one address
        var actor = await SeedSystemAdminAsync();
        await SpendAsync(null, "203.0.113.74", Startup.AccountCreationPermitLimit);

        // When an administrator creates from the same address
        using var response = await CreateAsync(actor, "203.0.113.74");

        // Then — anonymous traffic does not spend the address bound administrators share
        Assert.NotEqual(HttpStatusCode.TooManyRequests, response.StatusCode);
    }

    [FunctionalTheory]
    [InlineData("user")]
    [InlineData("challenge")]
    [InlineData("subject-rights")]
    public async Task GivenNonAdministratorTokensSpentAnAddressesMinute_WhenAnAdministratorCreatesFromIt_ThenTheyAreServed(
        string kind)
    {
        // Given thirty requests from one address with a token the endpoint refuses: a User's, an
        // administrator's two-factor challenge, or an administrator's subject-rights-only token
        var actor = await SeedSystemAdminAsync();
        var (address, token) = kind switch
        {
            "user" => ("203.0.113.75", TestTokens.For(await SeedUserAsync(), (int)Roles.User)),
            "challenge" => ("203.0.113.76",
                TestTokens.ForMfaPending(await SeedSystemAdminAsync(), (int)Roles.SystemAdmin)),
            _ => ("203.0.113.77", await SubjectRightsTokenAsync())
        };

        for (var attempt = 1; attempt <= Startup.AccountCreationPermitLimit; attempt++)
        {
            using var refused = await CreateWithTokenAsync(token, address);
            Assert.NotEqual(HttpStatusCode.TooManyRequests, refused.StatusCode);
        }

        // When an administrator creates from the same address
        using var response = await CreateAsync(actor, address);

        // Then — only administrators' access tokens spend the address bound they share
        Assert.NotEqual(HttpStatusCode.TooManyRequests, response.StatusCode);
    }
}
