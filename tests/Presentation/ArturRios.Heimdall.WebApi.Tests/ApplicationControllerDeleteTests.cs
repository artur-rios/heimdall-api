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
using Application = ArturRios.Heimdall.Domain.Entities.Application;

namespace ArturRios.Heimdall.WebApi.Tests;

// Functional tests for DELETE /api/scopes/{scopeId}/applications/{id} (UC-19, FR-AP-07): the main
// flow for a System Admin and for the owning Scope Admin, AF-19a (unknown id, wrong scope), AF-19b
// (already deleted — directly, by a repeated call, or by UC-04's scope cascade), AF-19c (a co-owner
// of the scope who does not own the application), and the framework-level flows (403 for a User, 401
// unauthenticated).
[Collection(nameof(FunctionalCollection))]
public class ApplicationControllerDeleteTests(PostgresFixture db) : WebApiTest<Program>(EnvironmentType.Local)
{
    // A fixed, obviously-not-now timestamp, so "UpdatedAt was (not) stamped" is a meaningful assertion.
    private static readonly DateTime Stamp = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static string Route(Guid scopeId, Guid id) => $"/api/scopes/{scopeId}/applications/{id}";

    private async Task<Scope> SeedScopeAsync(bool isDeleted = false)
    {
        await using var context = db.CreateContext();
        var scope = new Scope { PublicId = Guid.NewGuid(), Name = $"scope-{Guid.NewGuid():N}", IsDeleted = isDeleted };
        context.Scopes.Add(scope);
        await context.SaveChangesAsync();
        return scope;
    }

    private async Task<Person> SeedScopeAdminAsync(Scope? ownedScope = null)
    {
        await using var context = db.CreateContext();
        var person = new Person
        {
            PublicId = Guid.NewGuid(), Name = "Admin",
            Email = $"admin-{Guid.NewGuid():N}@test.local",
            RoleId = (long)Roles.ScopeAdmin, EmailVerified = true
        };
        context.Persons.Add(person);
        await context.SaveChangesAsync();

        if (ownedScope is not null)
        {
            context.ScopeOwners.Add(new ScopeOwner { ScopeId = ownedScope.Id, PersonId = person.Id });
            await context.SaveChangesAsync();
        }

        return person;
    }

    private async Task<Person> SeedUserAsync(Scope scope)
    {
        await using var context = db.CreateContext();
        var person = new Person
        {
            PublicId = Guid.NewGuid(), Name = "User",
            Email = $"user-{Guid.NewGuid():N}@test.local",
            RoleId = (long)Roles.User, EmailVerified = true
        };
        context.Persons.Add(person);
        await context.SaveChangesAsync();

        context.ScopeUsers.Add(new ScopeUser { ScopeId = scope.Id, PersonId = person.Id });
        await context.SaveChangesAsync();

        return person;
    }

    private async Task<Application> SeedApplicationAsync(Scope scope, Person owner, bool isDeleted = false)
    {
        await using var context = db.CreateContext();
        var application = new Application
        {
            PublicId = Guid.NewGuid(),
            Name = $"app-{Guid.NewGuid():N}",
            IsDeleted = isDeleted,
            ScopeId = scope.Id,
            OwnerId = owner.Id,
            UpdatedAt = Stamp
        };
        context.Applications.Add(application);
        await context.SaveChangesAsync();
        return application;
    }

    private async Task<Application> StoredAsync(Guid publicId)
    {
        await using var context = db.CreateContext();
        return await context.Applications.AsNoTracking().FirstAsync(a => a.PublicId == publicId);
    }

    [FunctionalFact]
    public async Task GivenSystemAdmin_WhenDeleteApplication_ThenOkAndRowIsFlagged()
    {
        // Given an application a System Admin does not own
        var scope = await SeedScopeAsync();
        var owner = await SeedScopeAdminAsync(ownedScope: scope);
        var application = await SeedApplicationAsync(scope, owner);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.DeleteAsync<DataOutput<DeleteApplicationCommandOutput?>>(
            Route(scope.PublicId, application.PublicId));

        // Then — response carries the public identifier and the performed-now flag
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(application.PublicId, response.Body?.Data?.Id);
        Assert.False(response.Body?.Data?.AlreadyDeleted);

        // Then — database state: flagged and UpdatedAt stamped
        var stored = await StoredAsync(application.PublicId);
        Assert.True(stored.IsDeleted);
        Assert.NotEqual(Stamp, stored.UpdatedAt);
    }

    [FunctionalFact]
    public async Task GivenOwningScopeAdmin_WhenDeleteApplication_ThenOkAndRowIsFlagged()
    {
        // Given the Scope Admin who owns the application (UC-19 step 2)
        var scope = await SeedScopeAsync();
        var owner = await SeedScopeAdminAsync(ownedScope: scope);
        var application = await SeedApplicationAsync(scope, owner);
        Authorize(TestTokens.For(owner.PublicId, (int)Roles.ScopeAdmin));

        // When
        var response = await Gateway.DeleteAsync<DataOutput<DeleteApplicationCommandOutput?>>(
            Route(scope.PublicId, application.PublicId));

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Body?.Data?.AlreadyDeleted);
        Assert.True((await StoredAsync(application.PublicId)).IsDeleted);
    }

    [FunctionalFact]
    public async Task GivenAlreadyDeletedApplication_WhenDeleteApplication_ThenOkAndNothingChanges()
    {
        // Given an application that is already logically deleted (AF-19b)
        var scope = await SeedScopeAsync();
        var owner = await SeedScopeAdminAsync(ownedScope: scope);
        var application = await SeedApplicationAsync(scope, owner, isDeleted: true);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.DeleteAsync<DataOutput<DeleteApplicationCommandOutput?>>(
            Route(scope.PublicId, application.PublicId));

        // Then — the same 200 as the main flow, and nothing was written
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Body?.Data?.AlreadyDeleted);

        var stored = await StoredAsync(application.PublicId);
        Assert.True(stored.IsDeleted);
        Assert.Equal(Stamp, stored.UpdatedAt);
    }

    [FunctionalFact]
    public async Task GivenApplicationDeletedTwice_WhenDeleteApplication_ThenSecondCallReportsAlreadyDeleted()
    {
        // Given the endpoint called twice for the same application (AF-19b: the call is idempotent)
        var scope = await SeedScopeAsync();
        var owner = await SeedScopeAdminAsync(ownedScope: scope);
        var application = await SeedApplicationAsync(scope, owner);
        Authorize(TestTokens.For(owner.PublicId, (int)Roles.ScopeAdmin));

        // When
        var first = await Gateway.DeleteAsync<DataOutput<DeleteApplicationCommandOutput?>>(
            Route(scope.PublicId, application.PublicId));
        var deletedAt = (await StoredAsync(application.PublicId)).UpdatedAt;
        var second = await Gateway.DeleteAsync<DataOutput<DeleteApplicationCommandOutput?>>(
            Route(scope.PublicId, application.PublicId));

        // Then — both succeed identically; only the flag and the untouched timestamp differ
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.False(first.Body?.Data?.AlreadyDeleted);
        Assert.True(second.Body?.Data?.AlreadyDeleted);
        Assert.Equal(first.Body?.Messages, second.Body?.Messages);
        Assert.Equal(deletedAt, (await StoredAsync(application.PublicId)).UpdatedAt);
    }

    [FunctionalFact]
    public async Task GivenApplicationDeletedByItsScopeCascade_WhenDeleteApplication_ThenOkAndAlreadyDeleted()
    {
        // Given an application already carrying IsDeleted from UC-04's cascade off its scope: the
        // handler does not consult the scope's own state, it just finds the deleted row (AF-19b)
        var scope = await SeedScopeAsync(isDeleted: true);
        var owner = await SeedScopeAdminAsync(ownedScope: scope);
        var application = await SeedApplicationAsync(scope, owner, isDeleted: true);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.DeleteAsync<DataOutput<DeleteApplicationCommandOutput?>>(
            Route(scope.PublicId, application.PublicId));

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Body?.Data?.AlreadyDeleted);
        Assert.Equal(Stamp, (await StoredAsync(application.PublicId)).UpdatedAt);
    }

    [FunctionalFact]
    public async Task GivenScopeAdminWhoOwnsTheScopeButNotTheApplication_WhenDeleteApplication_ThenForbidden()
    {
        // Given a co-owner of the scope: owning the scope is not grounds to delete another owner's
        // application (AF-19c)
        var scope = await SeedScopeAsync();
        var owner = await SeedScopeAdminAsync(ownedScope: scope);
        var coOwner = await SeedScopeAdminAsync(ownedScope: scope);
        var application = await SeedApplicationAsync(scope, owner);
        Authorize(TestTokens.For(coOwner.PublicId, (int)Roles.ScopeAdmin));

        // When
        var response = await Gateway.DeleteAsync<DataOutput<DeleteApplicationCommandOutput?>>(
            Route(scope.PublicId, application.PublicId));

        // Then — refused, and the row is still active
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        var stored = await StoredAsync(application.PublicId);
        Assert.False(stored.IsDeleted);
        Assert.Equal(Stamp, stored.UpdatedAt);
    }

    [FunctionalFact]
    public async Task GivenUserRole_WhenDeleteApplication_ThenForbidden()
    {
        // Given a caller holding the User role: FR-AP-03 lets them own no application, so the
        // endpoint's [RoleRequirement] refuses them
        var scope = await SeedScopeAsync();
        var owner = await SeedScopeAdminAsync(ownedScope: scope);
        var application = await SeedApplicationAsync(scope, owner);
        var caller = await SeedUserAsync(scope);
        Authorize(TestTokens.For(caller.PublicId, (int)Roles.User));

        // When
        var response = await Gateway.DeleteAsync<DataOutput<DeleteApplicationCommandOutput?>>(
            Route(scope.PublicId, application.PublicId));

        // Then
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False((await StoredAsync(application.PublicId)).IsDeleted);
    }

    [FunctionalFact]
    public async Task GivenUnknownApplication_WhenDeleteApplication_ThenNotFound()
    {
        // Given an application id nobody holds (AF-19a)
        var scope = await SeedScopeAsync();
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.DeleteAsync<DataOutput<DeleteApplicationCommandOutput?>>(
            Route(scope.PublicId, Guid.NewGuid()));

        // Then
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenApplicationOfAnotherScope_WhenDeleteApplication_ThenNotFound()
    {
        // Given the application exists, but under a different scope than the path addresses (AF-19a)
        var scope = await SeedScopeAsync();
        var otherScope = await SeedScopeAsync();
        var owner = await SeedScopeAdminAsync(ownedScope: otherScope);
        var application = await SeedApplicationAsync(otherScope, owner);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.DeleteAsync<DataOutput<DeleteApplicationCommandOutput?>>(
            Route(scope.PublicId, application.PublicId));

        // Then — refused, and the row is untouched
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.False((await StoredAsync(application.PublicId)).IsDeleted);
    }

    [FunctionalFact]
    public async Task GivenNoToken_WhenDeleteApplication_ThenUnauthorized()
    {
        // Given no bearer token (precondition: the actor is authenticated)
        var scope = await SeedScopeAsync();

        // When
        var response = await Gateway.DeleteAsync<DataOutput<DeleteApplicationCommandOutput?>>(
            Route(scope.PublicId, Guid.NewGuid()));

        // Then
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
