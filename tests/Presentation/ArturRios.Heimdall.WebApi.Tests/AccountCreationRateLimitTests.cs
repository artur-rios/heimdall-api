using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ArturRios.Configuration.Enums;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Heimdall.WebApi.Tests.Support;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Functional;

namespace ArturRios.Heimdall.WebApi.Tests;

// Tests for the rate limit on the endpoints that create a person with a password (Threat Model TH-03).
// Each derives an Argon2id hash through the process-wide gate, and each needs nothing but an
// administrator's token, so without a bound one account — from one address — could keep the gate full
// and starve every sign-in. The limit is per acting person, sized for real administration.
//
// The functional test spends the budget with malformed bodies: the limiter counts a request before MVC
// sees it, so a 400 costs a slot as a created person does, without thirty derivations.
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

    private async Task<HttpResponseMessage> CreateAsync(Guid actor)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/persons")
        {
            Content = JsonContent.Create(new { })
        };

        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", TestTokens.For(actor, (int)Roles.SystemAdmin));

        return await Gateway.Client.SendAsync(request);
    }

    private static async Task<string?> FirstErrorAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        return document.RootElement.TryGetProperty("errors", out var errors)
            ? errors.EnumerateArray().Select(error => error.GetString()).FirstOrDefault()
            : null;
    }

    [FunctionalFact]
    public async Task GivenOneActorSpentTheMinutesBudget_WhenTheyCreateAgain_ThenTooManyRequestsAndAnotherActorIsServed()
    {
        // Given two System Admins, acting from the same address
        var first = await SeedSystemAdminAsync();
        var second = await SeedSystemAdminAsync();

        for (var attempt = 1; attempt <= Startup.AccountCreationPermitLimit; attempt++)
        {
            using var allowed = await CreateAsync(first);
            Assert.NotEqual(HttpStatusCode.TooManyRequests, allowed.StatusCode);
        }

        // When
        using var limited = await CreateAsync(first);
        using var other = await CreateAsync(second);

        // Then — per actor, whatever address both come from
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.NotNull(limited.Headers.RetryAfter);
        Assert.Equal(RateLimitMessages.TooManyRequests, await FirstErrorAsync(limited));
        Assert.NotEqual(HttpStatusCode.TooManyRequests, other.StatusCode);
    }
}
