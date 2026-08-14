using System.Net;
using ArturRios.Configuration.Enums;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Command.Services;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Heimdall.WebApi.Tests.Support;
using ArturRios.Output;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Functional;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Heimdall.WebApi.Tests;

// Functional tests for DELETE /api/persons/{id}/hard (UC-10): the main flow for a User with every kind
// of dependent and for a co-owned ScopeAdmin, an already logically deleted person, AF-10a (404),
// AF-10b (409, NFR-12), AF-10c (403, self-deletion), plus the [RoleRequirement] gate (403) and the
// unauthenticated flow (401). Asserts response and database state, including the join-row cascade the
// unit tests cannot observe.
[Collection(nameof(FunctionalCollection))]
public class PersonControllerHardDeleteTests(PostgresFixture db) : WebApiTest<Program>(EnvironmentType.Local)
{
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
            IsDeleted = isDeleted
        };
        context.Persons.Add(person);
        await context.SaveChangesAsync();

        context.ScopeUsers.Add(new ScopeUser { ScopeId = scope.Id, PersonId = person.Id });
        // Person.ScopeId mirrors the membership row, as the application writes it — without
        // it the seeded User sits outside the per-scope uniqueness index.
        person.ScopeId = scope.Id;
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
            EmailVerified = true
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

    private async Task SeedTokensAsync(Person person)
    {
        await using var context = db.CreateContext();
        context.PasswordResetTokens.Add(new PasswordResetToken
        {
            PersonId = person.Id, TokenHash = SingleUseTokenHash.Of(Guid.NewGuid().ToString("N")),
            ExpiresAt = DateTime.UtcNow.AddHours(1)
        });
        context.EmailVerificationTokens.Add(new EmailVerificationToken
        {
            PersonId = person.Id, TokenHash = SingleUseTokenHash.Of(Guid.NewGuid().ToString("N")),
            ExpiresAt = DateTime.UtcNow.AddHours(1)
        });
        await context.SaveChangesAsync();
    }

    private async Task<bool> PersonExistsAsync(Person person)
    {
        await using var context = db.CreateContext();
        return await context.Persons.AsNoTracking().AnyAsync(p => p.Id == person.Id);
    }

    [FunctionalFact]
    public async Task GivenSystemAdminAndPersonWithDependents_WhenHardDeletePerson_ThenPersonAndDependentsAreRemoved()
    {
        // Given a User owning an application, holding both token kinds, and a member of a scope
        var scope = await SeedScopeAsync();
        var person = await SeedUserAsync(scope);
        var application = await SeedApplicationAsync(scope, person);
        await SeedTokensAsync(person);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.DeleteAsync<DataOutput<HardDeletePersonCommandOutput?>>(
            $"/api/persons/{person.PublicId}/hard");

        // Then — response
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(person.PublicId, response.Body?.Data?.Id);
        Assert.Equal(1, response.Body?.Data?.DeletedApplicationCount);
        Assert.Equal(2, response.Body?.Data?.DeletedTokenCount);

        // Then — database state: the person, their application, tokens, and membership row are gone,
        // and the scope itself survives
        await using var context = db.CreateContext();
        Assert.False(await context.Persons.AsNoTracking().AnyAsync(p => p.Id == person.Id));
        Assert.False(await context.Applications.AsNoTracking().AnyAsync(a => a.Id == application.Id));
        Assert.False(await context.PasswordResetTokens.AsNoTracking().AnyAsync(t => t.PersonId == person.Id));
        Assert.False(await context.EmailVerificationTokens.AsNoTracking().AnyAsync(t => t.PersonId == person.Id));
        Assert.False(await context.ScopeUsers.AsNoTracking().AnyAsync(su => su.PersonId == person.Id));
        Assert.True(await context.Scopes.AsNoTracking().AnyAsync(s => s.Id == scope.Id));
    }

    [FunctionalFact]
    public async Task GivenCoOwnedScope_WhenHardDeletePerson_ThenOwnerAndOwnershipRowAreRemoved()
    {
        // Given a scope with a second owner, so NFR-12 still holds after the deletion
        var scope = await SeedScopeAsync();
        var target = await SeedScopeAdminAsync(ownedScope: scope);
        var coOwner = await SeedScopeAdminAsync(ownedScope: scope);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.DeleteAsync<DataOutput<HardDeletePersonCommandOutput?>>(
            $"/api/persons/{target.PublicId}/hard");

        // Then — the target and their ownership row are gone; the co-owner and the scope remain
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var context = db.CreateContext();
        Assert.False(await context.Persons.AsNoTracking().AnyAsync(p => p.Id == target.Id));
        Assert.False(await context.ScopeOwners.AsNoTracking().AnyAsync(so => so.PersonId == target.Id));
        Assert.True(await context.Persons.AsNoTracking().AnyAsync(p => p.Id == coOwner.Id));
        Assert.True(await context.ScopeOwners.AsNoTracking().AnyAsync(so => so.PersonId == coOwner.Id));
        Assert.True(await context.Scopes.AsNoTracking().AnyAsync(s => s.Id == scope.Id));
    }

    [FunctionalFact]
    public async Task GivenLogicallyDeletedPerson_WhenHardDeletePerson_ThenPersonIsRemoved()
    {
        // Given an already soft-deleted person: hard deletion works in any deletion state
        var scope = await SeedScopeAsync();
        var person = await SeedUserAsync(scope, isDeleted: true);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.DeleteAsync<DataOutput<HardDeletePersonCommandOutput?>>(
            $"/api/persons/{person.PublicId}/hard");

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(await PersonExistsAsync(person));
    }

    [FunctionalFact]
    public async Task GivenUnknownPersonId_WhenHardDeletePerson_ThenNotFound()
    {
        // Given — AF-10a
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.DeleteAsync<DataOutput<HardDeletePersonCommandOutput?>>(
            $"/api/persons/{Guid.NewGuid()}/hard");

        // Then
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenSoleOwnerScopeAdmin_WhenHardDeletePerson_ThenConflict()
    {
        // Given a scope whose only owner is the target (AF-10b, NFR-12)
        var scope = await SeedScopeAsync();
        var owner = await SeedScopeAdminAsync(ownedScope: scope);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.DeleteAsync<DataOutput<HardDeletePersonCommandOutput?>>(
            $"/api/persons/{owner.PublicId}/hard");

        // Then — refused, and both the person and their ownership row survive
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        await using var context = db.CreateContext();
        Assert.True(await context.Persons.AsNoTracking().AnyAsync(p => p.Id == owner.Id));
        Assert.True(await context.ScopeOwners.AsNoTracking().AnyAsync(so => so.PersonId == owner.Id));
    }

    [FunctionalFact]
    public async Task GivenActorTargetingThemselves_WhenHardDeletePerson_ThenForbidden()
    {
        // Given — AF-10c. The message is asserted because the role gate returns the same status.
        //
        // The actor is the fixture's stand-in System Admin rather than a seeded User carrying a
        // forged System Admin claim, which is how this read before ActorLivenessFilter began
        // comparing the role claim against the stored role (TH-08). That token no longer reaches a
        // handler at all — it is refused as out of date, which is the fix working — so the test
        // would have proven a 401 where it means to prove AF-10c's 403.
        var actorId = PostgresFixture.StandInPersonIds[Roles.SystemAdmin];
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.DeleteAsync<DataOutput<HardDeletePersonCommandOutput?>>(
            $"/api/persons/{actorId}/hard");

        // Then
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains(PersonMessages.CannotDeleteSelf, response.Body?.Errors ?? []);

        await using var context = db.CreateContext();
        Assert.True(await context.Persons.AsNoTracking().AnyAsync(person => person.PublicId == actorId));
    }

    [FunctionalFact]
    public async Task GivenScopeAdminRole_WhenHardDeletePerson_ThenForbidden()
    {
        // Given a Scope Admin, whom the [RoleRequirement] gate keeps out entirely — unlike UC-09's
        // logical delete, hard deletion is System-Admin-only
        var scope = await SeedScopeAsync();
        var owner = await SeedScopeAdminAsync(ownedScope: scope);
        var person = await SeedUserAsync(scope);
        Authorize(TestTokens.For(owner.PublicId, (int)Roles.ScopeAdmin));

        // When
        var response = await Gateway.DeleteAsync<DataOutput<HardDeletePersonCommandOutput?>>(
            $"/api/persons/{person.PublicId}/hard");

        // Then
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.True(await PersonExistsAsync(person));
    }

    [FunctionalFact]
    public async Task GivenUserRole_WhenHardDeletePerson_ThenForbidden()
    {
        // Given a plain User
        var scope = await SeedScopeAsync();
        var person = await SeedUserAsync(scope);
        Authorize(TestTokens.ForRole((int)Roles.User));

        // When
        var response = await Gateway.DeleteAsync<DataOutput<HardDeletePersonCommandOutput?>>(
            $"/api/persons/{person.PublicId}/hard");

        // Then
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.True(await PersonExistsAsync(person));
    }

    [FunctionalFact]
    public async Task GivenNoToken_WhenHardDeletePerson_ThenUnauthorized()
    {
        // Given a person but no bearer token on the gateway
        var scope = await SeedScopeAsync();
        var person = await SeedUserAsync(scope);

        // When
        var response = await Gateway.DeleteAsync<DataOutput<HardDeletePersonCommandOutput?>>(
            $"/api/persons/{person.PublicId}/hard");

        // Then
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.True(await PersonExistsAsync(person));
    }
}
