using System.Net;
using ArturRios.Configuration.Enums;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.WebApi.Tests.Support;
using ArturRios.Output;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Functional;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Heimdall.WebApi.Tests;

[Collection(nameof(FunctionalCollection))]
public class ScopeControllerDeleteTests(PostgresFixture db) : WebApiTest<Program>(EnvironmentType.Local)
{
    private static string UniqueName() => $"scope-{Guid.NewGuid():N}";

    private async Task<GoogleUser> SeedGoogleUserAsync(Scope scope)
    {
        await using var context = db.CreateContext();
        var googleUser = new GoogleUser
        {
            PublicId = Guid.NewGuid(),
            GoogleId = Guid.NewGuid().ToString("N"),
            Name = "Google User",
            Email = $"google-{Guid.NewGuid():N}@test.local",
            ScopeId = scope.Id
        };
        context.GoogleUsers.Add(googleUser);
        await context.SaveChangesAsync();
        return googleUser;
    }

    private async Task<Application> SeedApplicationAsync(Scope scope, Person owner)
    {
        await using var context = db.CreateContext();
        var application = new Application
        {
            PublicId = Guid.NewGuid(),
            Name = $"app-{Guid.NewGuid():N}",
            ScopeId = scope.Id,
            OwnerId = owner.Id
        };
        context.Applications.Add(application);
        await context.SaveChangesAsync();
        return application;
    }

    [FunctionalFact]
    public async Task GivenSystemAdminAndScopeWithMembers_WhenDeleteScope_ThenScopeAndMembersAreLogicallyDeleted()
    {
        // Given a scope with an owner, two Users, one Google User, and one application
        var scope = await Seeds.ScopeAsync(db, new ScopeOptions { Name = UniqueName() });
        var owner = await Seeds.PersonAsync(db, Roles.ScopeAdmin, new PersonOptions { EmailPrefix = "person" });
        var user1 = await Seeds.PersonAsync(db, Roles.User, new PersonOptions { EmailPrefix = "person", MemberOf = scope });
        var user2 = await Seeds.PersonAsync(db, Roles.User, new PersonOptions { EmailPrefix = "person", MemberOf = scope });
        var googleUser = await SeedGoogleUserAsync(scope);
        var application = await SeedApplicationAsync(scope, owner);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.DeleteAsync<DataOutput<DeleteScopeCommandOutput?>>(
            $"/api/scopes/{scope.PublicId}");

        // Then — response
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(scope.PublicId, response.Body?.Data?.Id);
        Assert.Equal(2, response.Body?.Data?.UserCount);
        Assert.Equal(1, response.Body?.Data?.GoogleUserCount);
        Assert.Equal(1, response.Body?.Data?.ApplicationCount);

        // Then — database state
        await using var context = db.CreateContext();
        Assert.True((await context.Scopes.AsNoTracking().FirstAsync(x => x.Id == scope.Id)).IsDeleted);
        Assert.True((await context.Persons.AsNoTracking().FirstAsync(x => x.Id == user1.Id)).IsDeleted);
        Assert.True((await context.Persons.AsNoTracking().FirstAsync(x => x.Id == user2.Id)).IsDeleted);
        Assert.True((await context.GoogleUsers.AsNoTracking().FirstAsync(x => x.Id == googleUser.Id)).IsDeleted);
        Assert.True((await context.Applications.AsNoTracking().FirstAsync(x => x.Id == application.Id)).IsDeleted);
        // The owner (ScopeAdmin) is not modified.
        Assert.False((await context.Persons.AsNoTracking().FirstAsync(x => x.Id == owner.Id)).IsDeleted);
    }

    [FunctionalFact]
    public async Task GivenUnknownScopeId_WhenDeleteScope_ThenNotFound()
    {
        // Given
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.DeleteAsync<DataOutput<DeleteScopeCommandOutput?>>(
            $"/api/scopes/{Guid.NewGuid()}");

        // Then
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenAlreadyDeletedScope_WhenDeleteScope_ThenOkAndMembersUnchanged()
    {
        // Given an already logically deleted scope with one (already deleted) application
        var scope = await Seeds.ScopeAsync(db, new ScopeOptions { Name = UniqueName(), IsDeleted = true });
        var owner = await Seeds.PersonAsync(db, Roles.ScopeAdmin, new PersonOptions { EmailPrefix = "person" });
        var application = await SeedApplicationAsync(scope, owner);
        await using (var seedContext = db.CreateContext())
        {
            var app = await seedContext.Applications.FirstAsync(x => x.Id == application.Id);
            app.IsDeleted = true;
            await seedContext.SaveChangesAsync();
        }
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.DeleteAsync<DataOutput<DeleteScopeCommandOutput?>>(
            $"/api/scopes/{scope.PublicId}");

        // Then — idempotent success, totals still reported
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, response.Body?.Data?.ApplicationCount);
    }

    [FunctionalFact]
    public async Task GivenNonSystemAdmin_WhenDeleteScope_ThenForbidden()
    {
        // Given
        var scope = await Seeds.ScopeAsync(db, new ScopeOptions { Name = UniqueName() });
        Authorize(TestTokens.ForRole((int)Roles.User));

        // When
        var response = await Gateway.DeleteAsync<DataOutput<DeleteScopeCommandOutput?>>(
            $"/api/scopes/{scope.PublicId}");

        // Then
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenNoToken_WhenDeleteScope_ThenUnauthorized()
    {
        // Given a scope but no bearer token on the gateway
        var scope = await Seeds.ScopeAsync(db, new ScopeOptions { Name = UniqueName() });

        // When
        var response = await Gateway.DeleteAsync<DataOutput<DeleteScopeCommandOutput?>>(
            $"/api/scopes/{scope.PublicId}");

        // Then
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
