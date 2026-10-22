using System.Net;
using ArturRios.Configuration.Enums;
using ArturRios.Heimdall.Command.Services;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Query.Output;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Heimdall.WebApi.Tests.Support;
using ArturRios.Output;
using ArturRios.Util.Http;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Functional;

namespace ArturRios.Heimdall.WebApi.Tests;

// Functional tests for GET /api/scopes/{id}/pending-registrations (UC-49, FR-SR-11): the main flow for
// a System Admin and an owning Scope Admin, the wire never carrying a secret, AF-49a (unknown or deleted
// scope → 404), AF-49b (non-owner → 403), AF-49c (empty page), and the framework's 403 for a User and
// 401 anonymous.
[Collection(nameof(FunctionalCollection))]
public class ScopeControllerListPendingRegistrationsTests(PostgresFixture db) : WebApiTest<Program>(EnvironmentType.Local)
{
    private static string Route(Guid scopeId) => $"/api/scopes/{scopeId}/pending-registrations?pageNumber=1&pageSize=20";

    private async Task<PendingRegistration> SeedPendingAsync(Scope scope, DateTime createdAt, int failedAttempts = 0)
    {
        await using var context = db.CreateContext();
        var pending = new PendingRegistration
        {
            ScopeId = scope.Id,
            Name = "Registrant",
            Email = $"registrant-{Guid.NewGuid():N}@test.local",
            PasswordHash = [1],
            Salt = [2],
            TokenHash = SingleUseTokenHash.Of(SingleUseToken.New()),
            CreatedAt = createdAt,
            ExpiresAt = createdAt.AddDays(1),
            FailedAttempts = failedAttempts
        };
        context.PendingRegistrations.Add(pending);
        await context.SaveChangesAsync();
        return pending;
    }

    private Task<HttpOutput<PaginatedOutput<PendingRegistrationOutput>?>> ListAsync(Guid scopeId) =>
        Gateway.GetAsync<PaginatedOutput<PendingRegistrationOutput>>(Route(scopeId));

    [FunctionalFact]
    public async Task GivenSystemAdmin_WhenListing_ThenTheScopesRowsComeBackNewestFirst()
    {
        // Given two registrations in the scope and one in another
        var scope = await Seeds.ScopeAsync(db, new ScopeOptions { SelfRegistrationEnabled = true });
        var older = await SeedPendingAsync(scope, DateTime.UtcNow.AddMinutes(-20), failedAttempts: 4);
        var newer = await SeedPendingAsync(scope, DateTime.UtcNow.AddMinutes(-10));
        await SeedPendingAsync(await Seeds.ScopeAsync(db, new ScopeOptions { SelfRegistrationEnabled = true }), DateTime.UtcNow);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await ListAsync(scope.PublicId);

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(ScopeMessages.PendingRegistrationsRetrieved, response.Body!.Messages);
        Assert.Equal([newer.PublicId, older.PublicId], response.Body.Data!.Select(x => x.Id));
        var oldest = response.Body.Data!.Last();
        Assert.Equal(older.Email, oldest.Email);
        Assert.Equal(PendingRegistration.MaxConfirmationGuesses - 4, oldest.AttemptsLeft);
        Assert.False(oldest.Expired);
    }

    [FunctionalFact]
    public async Task GivenOwningScopeAdmin_WhenListing_ThenTheyMayReadIt()
    {
        // Given an owner of the scope
        var scope = await Seeds.ScopeAsync(db, new ScopeOptions { SelfRegistrationEnabled = true });
        var owner = await Seeds.PersonAsync(db, Roles.ScopeAdmin, new PersonOptions { EmailPrefix = "owner", Owns = [scope] });
        var row = await SeedPendingAsync(scope, DateTime.UtcNow);
        Authorize(TestTokens.For(owner.PublicId, (int)Roles.ScopeAdmin));

        // When
        var response = await ListAsync(scope.PublicId);

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(row.PublicId, Assert.Single(response.Body!.Data!).Id);
    }

    [FunctionalFact]
    public async Task GivenAScopeClosedToSelfRegistration_WhenListing_ThenItsRowsAreStillListed()
    {
        // Given a scope whose sign-up was closed after a registration was stored
        var scope = await Seeds.ScopeAsync(db, new ScopeOptions { SelfRegistrationEnabled = false });
        var row = await SeedPendingAsync(scope, DateTime.UtcNow);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await ListAsync(scope.PublicId);

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(row.PublicId, Assert.Single(response.Body!.Data!).Id);
    }

    [FunctionalFact]
    public async Task GivenARow_WhenListing_ThenTheWireCarriesNoSecret()
    {
        // Given a registration with a hash, a salt and a token hash stored
        var scope = await Seeds.ScopeAsync(db, new ScopeOptions { SelfRegistrationEnabled = true });
        await SeedPendingAsync(scope, DateTime.UtcNow);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When — read raw, since the typed body could not show a field it does not declare
        var json = await Gateway.Client.GetStringAsync(Route(scope.PublicId));

        // Then
        Assert.DoesNotContain("passwordHash", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("salt", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tokenHash", json, StringComparison.OrdinalIgnoreCase);
    }

    [FunctionalFact]
    public async Task GivenNoPendingRows_WhenListing_ThenReturnsAnEmptyPage()
    {
        // Given AF-49c
        var scope = await Seeds.ScopeAsync(db, new ScopeOptions { SelfRegistrationEnabled = true });
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await ListAsync(scope.PublicId);

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(response.Body!.Data!);
    }

    [FunctionalFact]
    public async Task GivenAnUnknownScope_WhenListing_ThenReturnsNotFound()
    {
        // Given AF-49a
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await ListAsync(Guid.NewGuid());

        // Then
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains(ScopeMessages.ScopeNotFound, response.Body!.Errors);
    }

    [FunctionalFact]
    public async Task GivenALogicallyDeletedScope_WhenListing_ThenReturnsNotFound()
    {
        // Given AF-49a
        var scope = await Seeds.ScopeAsync(db, new ScopeOptions { IsDeleted = true, SelfRegistrationEnabled = true });
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await ListAsync(scope.PublicId);

        // Then
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenAScopeAdminWhoDoesNotOwnTheScope_WhenListing_ThenReturnsForbidden()
    {
        // Given AF-49b — an owner of a different scope
        var scope = await Seeds.ScopeAsync(db, new ScopeOptions { SelfRegistrationEnabled = true });
        var stranger = await Seeds.PersonAsync(db, Roles.ScopeAdmin, new PersonOptions { EmailPrefix = "owner", Owns = [await Seeds.ScopeAsync(db, new ScopeOptions { SelfRegistrationEnabled = true })] });
        await SeedPendingAsync(scope, DateTime.UtcNow);
        Authorize(TestTokens.For(stranger.PublicId, (int)Roles.ScopeAdmin));

        // When
        var response = await ListAsync(scope.PublicId);

        // Then
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains(ScopeMessages.NotScopeOwner, response.Body!.Errors);
    }

    [FunctionalFact]
    public async Task GivenAUser_WhenListing_ThenReturnsForbidden()
    {
        // Given the role gate
        var scope = await Seeds.ScopeAsync(db, new ScopeOptions { SelfRegistrationEnabled = true });
        Authorize(TestTokens.ForRole((int)Roles.User));

        // When
        var response = await ListAsync(scope.PublicId);

        // Then
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenNoToken_WhenListing_ThenReturnsUnauthorized()
    {
        // Given no bearer token
        var scope = await Seeds.ScopeAsync(db, new ScopeOptions { SelfRegistrationEnabled = true });

        // When
        var response = await ListAsync(scope.PublicId);

        // Then
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
