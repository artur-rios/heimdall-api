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
public class ScopeControllerHardDeleteTests(PostgresFixture db) : WebApiTest<Program>(EnvironmentType.Local)
{
    private static string UniqueName() => $"scope-{Guid.NewGuid():N}";

    private async Task<Scope> SeedScopeAsync(bool isDeleted = false)
    {
        await using var context = db.CreateContext();
        var scope = new Scope { PublicId = Guid.NewGuid(), Name = UniqueName(), IsDeleted = isDeleted };
        context.Scopes.Add(scope);
        await context.SaveChangesAsync();
        return scope;
    }

    private async Task<Person> SeedPersonAsync(Roles role)
    {
        await using var context = db.CreateContext();
        var person = new Person
        {
            PublicId = Guid.NewGuid(),
            Name = "Person",
            Email = $"person-{Guid.NewGuid():N}@test.local",
            RoleId = (long)role,
            EmailVerified = true
        };
        context.Persons.Add(person);
        await context.SaveChangesAsync();
        return person;
    }

    private async Task<Person> SeedOwnerAsync(Scope scope)
    {
        var owner = await SeedPersonAsync(Roles.ScopeAdmin);
        await using var context = db.CreateContext();
        context.ScopeOwners.Add(new ScopeOwner { ScopeId = scope.Id, PersonId = owner.Id });
        await context.SaveChangesAsync();
        return owner;
    }

    private async Task<Person> SeedScopeUserAsync(Scope scope)
    {
        var user = await SeedPersonAsync(Roles.User);
        await using var context = db.CreateContext();
        context.ScopeUsers.Add(new ScopeUser { ScopeId = scope.Id, PersonId = user.Id });
        await context.SaveChangesAsync();
        return user;
    }

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
    public async Task GivenSystemAdminAndScopeWithMembers_WhenHardDeleteScope_ThenScopeAndMembersAreRemovedButOwnerRemains()
    {
        // Given a scope with an owner, two Users, one Google User, and one application
        var scope = await SeedScopeAsync();
        var owner = await SeedOwnerAsync(scope);
        var user1 = await SeedScopeUserAsync(scope);
        var user2 = await SeedScopeUserAsync(scope);
        var googleUser = await SeedGoogleUserAsync(scope);
        var application = await SeedApplicationAsync(scope, owner);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.DeleteAsync<DataOutput<HardDeleteScopeCommandOutput?>>(
            $"/api/scopes/{scope.PublicId}/hard");

        // Then — response
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(scope.PublicId, response.Body?.Data?.Id);
        Assert.Equal(2, response.Body?.Data?.UserCount);
        Assert.Equal(1, response.Body?.Data?.GoogleUserCount);
        Assert.Equal(1, response.Body?.Data?.ApplicationCount);

        // Then — database state: the scope, its members, and its join rows are gone
        await using var context = db.CreateContext();
        Assert.False(await context.Scopes.AsNoTracking().AnyAsync(x => x.Id == scope.Id));
        Assert.False(await context.Persons.AsNoTracking().AnyAsync(x => x.Id == user1.Id));
        Assert.False(await context.Persons.AsNoTracking().AnyAsync(x => x.Id == user2.Id));
        Assert.False(await context.GoogleUsers.AsNoTracking().AnyAsync(x => x.Id == googleUser.Id));
        Assert.False(await context.Applications.AsNoTracking().AnyAsync(x => x.Id == application.Id));
        Assert.False(await context.ScopeOwners.AsNoTracking().AnyAsync(x => x.ScopeId == scope.Id));
        Assert.False(await context.ScopeUsers.AsNoTracking().AnyAsync(x => x.ScopeId == scope.Id));
        // The owner (ScopeAdmin) person record itself is NOT removed.
        Assert.True(await context.Persons.AsNoTracking().AnyAsync(x => x.Id == owner.Id));
    }

    [FunctionalFact]
    public async Task GivenUnknownScopeId_WhenHardDeleteScope_ThenNotFound()
    {
        // Given
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.DeleteAsync<DataOutput<HardDeleteScopeCommandOutput?>>(
            $"/api/scopes/{Guid.NewGuid()}/hard");

        // Then
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenLogicallyDeletedScope_WhenHardDeleteScope_ThenOkAndScopeRemoved()
    {
        // Given an already logically deleted scope with one application
        var scope = await SeedScopeAsync(isDeleted: true);
        var owner = await SeedOwnerAsync(scope);
        var application = await SeedApplicationAsync(scope, owner);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.DeleteAsync<DataOutput<HardDeleteScopeCommandOutput?>>(
            $"/api/scopes/{scope.PublicId}/hard");

        // Then — success; the scope and its application are gone, the owner person remains
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, response.Body?.Data?.ApplicationCount);

        await using var context = db.CreateContext();
        Assert.False(await context.Scopes.AsNoTracking().AnyAsync(x => x.Id == scope.Id));
        Assert.False(await context.Applications.AsNoTracking().AnyAsync(x => x.Id == application.Id));
        Assert.True(await context.Persons.AsNoTracking().AnyAsync(x => x.Id == owner.Id));
    }

    [FunctionalFact]
    public async Task GivenNonSystemAdmin_WhenHardDeleteScope_ThenForbidden()
    {
        // Given
        var scope = await SeedScopeAsync();
        Authorize(TestTokens.ForRole((int)Roles.User));

        // When
        var response = await Gateway.DeleteAsync<DataOutput<HardDeleteScopeCommandOutput?>>(
            $"/api/scopes/{scope.PublicId}/hard");

        // Then
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenNoToken_WhenHardDeleteScope_ThenUnauthorized()
    {
        // Given a scope but no bearer token on the gateway
        var scope = await SeedScopeAsync();

        // When
        var response = await Gateway.DeleteAsync<DataOutput<HardDeleteScopeCommandOutput?>>(
            $"/api/scopes/{scope.PublicId}/hard");

        // Then
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
