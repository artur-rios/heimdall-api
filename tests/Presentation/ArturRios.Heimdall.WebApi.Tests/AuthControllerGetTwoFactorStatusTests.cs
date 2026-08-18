using System.Net;
using ArturRios.Configuration.Enums;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Query.Output;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Heimdall.WebApi.Tests.Support;
using ArturRios.Output;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Functional;

namespace ArturRios.Heimdall.WebApi.Tests;

// Functional tests for GET /api/auth/2fa (FR-2F-15): the caller's own status over the real
// pipeline. Covers no configuration (200, all false), a pending setup, an active configuration
// with its unused recovery-code count, a Google User (403, AF-36b), and 401 unauthenticated.
[Collection(nameof(FunctionalCollection))]
public class AuthControllerGetTwoFactorStatusTests(PostgresFixture db) : WebApiTest<Program>(EnvironmentType.Local)
{
    private static string UniqueEmail(string prefix) => $"{prefix}-{Guid.NewGuid():N}@test.local";

    private async Task<Person> SeedPersonAsync()
    {
        await using var context = db.CreateContext();
        var person = new Person
        {
            PublicId = Guid.NewGuid(),
            Name = "Ana",
            Email = UniqueEmail("ana"),
            RoleId = (long)Roles.ScopeAdmin,
            EmailVerified = true
        };
        context.Persons.Add(person);
        await context.SaveChangesAsync();
        return person;
    }

    private async Task<GoogleUser> SeedGoogleUserAsync()
    {
        await using var context = db.CreateContext();
        var scope = new Scope { PublicId = Guid.NewGuid(), Name = $"scope-{Guid.NewGuid():N}" };
        context.Scopes.Add(scope);
        await context.SaveChangesAsync();

        var googleUser = new GoogleUser
        {
            PublicId = Guid.NewGuid(),
            GoogleId = $"google-{Guid.NewGuid():N}",
            Name = "Google User",
            Email = UniqueEmail("google"),
            EmailVerified = true,
            ScopeId = scope.Id,
            Scope = scope
        };
        context.GoogleUsers.Add(googleUser);
        await context.SaveChangesAsync();

        return googleUser;
    }

    private async Task SeedTwoFactorAsync(
        Person person, bool isActive, bool appEnabled, bool emailEnabled, int unusedCodes, int usedCodes)
    {
        await using var context = db.CreateContext();
        var configuration = new TwoFactorAuth
        {
            PersonId = person.Id,
            AppEnabled = appEnabled,
            EmailEnabled = emailEnabled,
            IsActive = isActive
        };
        context.TwoFactorAuths.Add(configuration);
        await context.SaveChangesAsync();

        for (var i = 0; i < unusedCodes + usedCodes; i++)
        {
            context.TwoFactorRecoveryCodes.Add(new TwoFactorRecoveryCode
            {
                TwoFactorAuthId = configuration.Id,
                CodeHash = [(byte)i],
                Used = i >= unusedCodes
            });
        }

        await context.SaveChangesAsync();
    }

    [FunctionalFact]
    public async Task GivenNoConfiguration_WhenGetTwoFactorStatus_ThenReturnsOkWithAllFalse()
    {
        // Given a person who never enabled two-factor authentication
        var person = await SeedPersonAsync();
        Authorize(TestTokens.For(person.PublicId, (int)Roles.ScopeAdmin));

        // When
        var response = await Gateway.GetAsync<DataOutput<TwoFactorStatusOutput?>>("/api/auth/2fa");

        // Then — a success, not a 404
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Body?.Data?.IsActive);
        Assert.False(response.Body?.Data?.AppEnabled);
        Assert.False(response.Body?.Data?.EmailEnabled);
        Assert.Equal(0, response.Body?.Data?.RemainingRecoveryCodes);
    }

    [FunctionalFact]
    public async Task GivenPendingSetup_WhenGetTwoFactorStatus_ThenReportsMethodsButNotActive()
    {
        // Given UC-36 initiated an app-method setup that UC-37 has not confirmed
        var person = await SeedPersonAsync();
        await SeedTwoFactorAsync(person, isActive: false, appEnabled: true, emailEnabled: false,
            unusedCodes: 0, usedCodes: 0);
        Authorize(TestTokens.For(person.PublicId, (int)Roles.ScopeAdmin));

        // When
        var response = await Gateway.GetAsync<DataOutput<TwoFactorStatusOutput?>>("/api/auth/2fa");

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Body?.Data?.IsActive);
        Assert.True(response.Body?.Data?.AppEnabled);
    }

    [FunctionalFact]
    public async Task GivenActiveConfiguration_WhenGetTwoFactorStatus_ThenCountsOnlyUnusedRecoveryCodes()
    {
        // Given an active configuration with ten codes, three of them already spent
        var person = await SeedPersonAsync();
        await SeedTwoFactorAsync(person, isActive: true, appEnabled: true, emailEnabled: true,
            unusedCodes: 7, usedCodes: 3);
        Authorize(TestTokens.For(person.PublicId, (int)Roles.ScopeAdmin));

        // When
        var response = await Gateway.GetAsync<DataOutput<TwoFactorStatusOutput?>>("/api/auth/2fa");

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Body?.Data?.IsActive);
        Assert.True(response.Body?.Data?.AppEnabled);
        Assert.True(response.Body?.Data?.EmailEnabled);
        Assert.Equal(7, response.Body?.Data?.RemainingRecoveryCodes);
    }

    [FunctionalFact]
    public async Task GivenUnauthenticatedCaller_WhenGetTwoFactorStatus_ThenReturnsUnauthorized()
    {
        // Given no bearer token
        // When
        var response = await Gateway.GetAsync<DataOutput<TwoFactorStatusOutput?>>("/api/auth/2fa");

        // Then
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenGoogleUser_WhenGetTwoFactorStatus_ThenReturnsForbidden()
    {
        // Given a live Google User's token — GoogleUser and Person are separate PublicId spaces,
        // so the person lookup misses and FR-2F-01 refuses them (AF-36b)
        var googleUser = await SeedGoogleUserAsync();
        Authorize(TestTokens.For(googleUser.PublicId, (int)Roles.User, googleUser.Scope.PublicId));

        // When
        var response = await Gateway.GetAsync<DataOutput<TwoFactorStatusOutput?>>("/api/auth/2fa");

        // Then — 403, not an all-false 200 that would imply they could turn it on
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains(TwoFactorMessages.NotEligible, response.Body!.Errors);
    }
}
