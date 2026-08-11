using System.Net;
using ArturRios.Configuration.Enums;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Heimdall.WebApi.Tests.Support;
using ArturRios.Output;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Functional;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Heimdall.WebApi.Tests;

// Functional tests for DELETE /api/persons/{id} (UC-09): the main flow for each permitted actor,
// AF-09a (404), AF-09b (idempotent 200), AF-09c (403), AF-09d (403, self-deletion), AF-09e (409,
// NFR-12), plus the [RoleRequirement] gate (403) and the unauthenticated flow (401). Asserts response
// and database state, including that a logical deletion cascades to nothing.
[Collection(nameof(FunctionalCollection))]
public class PersonControllerDeleteTests(PostgresFixture db) : WebApiTest<Program>(EnvironmentType.Local)
{
    // A fixed, obviously-not-now timestamp, so "UpdatedAt was not touched" is a meaningful assertion.
    private static readonly DateTime Stamp = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static string UniqueEmail(string prefix) => $"{prefix}-{Guid.NewGuid():N}@test.local";

    private async Task<Scope> SeedScopeAsync()
    {
        await using var context = db.CreateContext();
        var scope = new Scope { PublicId = Guid.NewGuid(), Name = $"scope-{Guid.NewGuid():N}" };
        context.Scopes.Add(scope);
        await context.SaveChangesAsync();
        return scope;
    }

    private async Task<Person> SeedUserAsync(Scope scope, bool isDeleted = false)
    {
        await using var context = db.CreateContext();
        var person = new Person
        {
            PublicId = Guid.NewGuid(),
            Name = "Member",
            Email = UniqueEmail("user"),
            RoleId = (long)Roles.User,
            EmailVerified = true,
            IsDeleted = isDeleted,
            CreatedAt = Stamp,
            UpdatedAt = Stamp
        };
        context.Persons.Add(person);
        await context.SaveChangesAsync();

        context.ScopeUsers.Add(new ScopeUser { ScopeId = scope.Id, PersonId = person.Id });
        await context.SaveChangesAsync();

        return person;
    }

    private async Task<Person> SeedScopeAdminAsync(Scope? ownedScope = null)
    {
        await using var context = db.CreateContext();
        var person = new Person
        {
            PublicId = Guid.NewGuid(),
            Name = "Admin",
            Email = UniqueEmail("admin"),
            RoleId = (long)Roles.ScopeAdmin,
            EmailVerified = true,
            CreatedAt = Stamp,
            UpdatedAt = Stamp
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

    private async Task<Person> StoredAsync(Person person)
    {
        await using var context = db.CreateContext();
        return await context.Persons.AsNoTracking().FirstAsync(p => p.Id == person.Id);
    }

    [FunctionalFact]
    public async Task GivenSystemAdmin_WhenDeletePerson_ThenPersonIsFlaggedDeleted()
    {
        // Given
        var scope = await SeedScopeAsync();
        var person = await SeedUserAsync(scope);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.DeleteAsync<DataOutput<DeletePersonCommandOutput?>>(
            $"/api/persons/{person.PublicId}");

        // Then — response
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(person.PublicId, response.Body?.Data?.Id);
        Assert.False(response.Body?.Data?.AlreadyDeleted);

        // Then — database state: flagged and stamped
        var stored = await StoredAsync(person);
        Assert.True(stored.IsDeleted);
        Assert.NotEqual(Stamp, stored.UpdatedAt);

        // Then — the deletion cascaded to nothing: the membership row survives
        await using var context = db.CreateContext();
        Assert.True(await context.ScopeUsers.AnyAsync(su => su.PersonId == person.Id));
    }

    [FunctionalFact]
    public async Task GivenOwningScopeAdmin_WhenDeletePerson_ThenUserIsFlaggedDeleted()
    {
        // Given a ScopeAdmin who owns the User's scope
        var scope = await SeedScopeAsync();
        var owner = await SeedScopeAdminAsync(ownedScope: scope);
        var person = await SeedUserAsync(scope);
        Authorize(TestTokens.For(owner.PublicId, (int)Roles.ScopeAdmin));

        // When
        var response = await Gateway.DeleteAsync<DataOutput<DeletePersonCommandOutput?>>(
            $"/api/persons/{person.PublicId}");

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Body?.Data?.AlreadyDeleted);
        Assert.True((await StoredAsync(person)).IsDeleted);
    }

    [FunctionalFact]
    public async Task GivenAlreadyDeletedPerson_WhenDeletePerson_ThenReturnsOkWithoutChangingTheRecord()
    {
        // Given — AF-09b
        var scope = await SeedScopeAsync();
        var person = await SeedUserAsync(scope, isDeleted: true);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.DeleteAsync<DataOutput<DeletePersonCommandOutput?>>(
            $"/api/persons/{person.PublicId}");

        // Then — an idempotent success that wrote nothing
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Body?.Data?.AlreadyDeleted);

        var stored = await StoredAsync(person);
        Assert.True(stored.IsDeleted);
        Assert.Equal(Stamp, stored.UpdatedAt);
    }

    [FunctionalFact]
    public async Task GivenUnknownPersonId_WhenDeletePerson_ThenNotFound()
    {
        // Given — AF-09a
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.DeleteAsync<DataOutput<DeletePersonCommandOutput?>>(
            $"/api/persons/{Guid.NewGuid()}");

        // Then
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenNonOwningScopeAdmin_WhenDeletePerson_ThenForbidden()
    {
        // Given a ScopeAdmin who does not own the User's scope (AF-09c)
        var scope = await SeedScopeAsync();
        var outsider = await SeedScopeAdminAsync();
        var person = await SeedUserAsync(scope);
        Authorize(TestTokens.For(outsider.PublicId, (int)Roles.ScopeAdmin));

        // When
        var response = await Gateway.DeleteAsync<DataOutput<DeletePersonCommandOutput?>>(
            $"/api/persons/{person.PublicId}");

        // Then — refused, and the person is untouched
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False((await StoredAsync(person)).IsDeleted);
    }

    [FunctionalFact]
    public async Task GivenScopeAdminTargetingAnotherScopeAdmin_WhenDeletePerson_ThenForbidden()
    {
        // Given two owners of the same scope: a Scope Admin may delete Users only (AF-09c)
        var scope = await SeedScopeAsync();
        var actor = await SeedScopeAdminAsync(ownedScope: scope);
        var target = await SeedScopeAdminAsync(ownedScope: scope);
        Authorize(TestTokens.For(actor.PublicId, (int)Roles.ScopeAdmin));

        // When
        var response = await Gateway.DeleteAsync<DataOutput<DeletePersonCommandOutput?>>(
            $"/api/persons/{target.PublicId}");

        // Then
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False((await StoredAsync(target)).IsDeleted);
    }

    [FunctionalFact]
    public async Task GivenActorTargetingThemselves_WhenDeletePerson_ThenForbidden()
    {
        // Given — AF-09d. The message is asserted because AF-09c returns the same status.
        var scope = await SeedScopeAsync();
        var actor = await SeedScopeAdminAsync(ownedScope: scope);
        Authorize(TestTokens.For(actor.PublicId, (int)Roles.ScopeAdmin));

        // When
        var response = await Gateway.DeleteAsync<DataOutput<DeletePersonCommandOutput?>>(
            $"/api/persons/{actor.PublicId}");

        // Then
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains(PersonMessages.CannotDeleteSelf, response.Body?.Errors ?? []);
        Assert.False((await StoredAsync(actor)).IsDeleted);
    }

    [FunctionalFact]
    public async Task GivenSoleOwnerScopeAdmin_WhenDeletePerson_ThenConflict()
    {
        // Given a scope whose only owner is the target (AF-09e, NFR-12)
        var scope = await SeedScopeAsync();
        var owner = await SeedScopeAdminAsync(ownedScope: scope);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.DeleteAsync<DataOutput<DeletePersonCommandOutput?>>(
            $"/api/persons/{owner.PublicId}");

        // Then — refused, and both the person and their ownership row survive
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var stored = await StoredAsync(owner);
        Assert.False(stored.IsDeleted);
        Assert.Equal(Stamp, stored.UpdatedAt);

        await using var context = db.CreateContext();
        Assert.True(await context.ScopeOwners.AnyAsync(so => so.PersonId == owner.Id));
    }

    [FunctionalFact]
    public async Task GivenCoOwnedScope_WhenDeletePerson_ThenOwnerIsFlaggedDeleted()
    {
        // Given a scope with a second owner, so NFR-12 still holds after the deletion
        var scope = await SeedScopeAsync();
        var target = await SeedScopeAdminAsync(ownedScope: scope);
        var coOwner = await SeedScopeAdminAsync(ownedScope: scope);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.DeleteAsync<DataOutput<DeletePersonCommandOutput?>>(
            $"/api/persons/{target.PublicId}");

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True((await StoredAsync(target)).IsDeleted);
        Assert.False((await StoredAsync(coOwner)).IsDeleted);
    }

    [FunctionalFact]
    public async Task GivenUserRole_WhenDeletePerson_ThenForbidden()
    {
        // Given a plain User, whom the [RoleRequirement] gate keeps out of the endpoint entirely
        var scope = await SeedScopeAsync();
        var person = await SeedUserAsync(scope);
        Authorize(TestTokens.ForRole((int)Roles.User));

        // When
        var response = await Gateway.DeleteAsync<DataOutput<DeletePersonCommandOutput?>>(
            $"/api/persons/{person.PublicId}");

        // Then
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False((await StoredAsync(person)).IsDeleted);
    }

    [FunctionalFact]
    public async Task GivenNoToken_WhenDeletePerson_ThenUnauthorized()
    {
        // Given a person but no bearer token on the gateway
        var scope = await SeedScopeAsync();
        var person = await SeedUserAsync(scope);

        // When
        var response = await Gateway.DeleteAsync<DataOutput<DeletePersonCommandOutput?>>(
            $"/api/persons/{person.PublicId}");

        // Then
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
