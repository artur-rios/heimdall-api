using System.Net;
using ArturRios.Configuration.Enums;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.WebApi.Tests.Support;
using ArturRios.Output;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Functional;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Heimdall.WebApi.Tests;

// Functional tests for PUT /api/persons/{id} (UC-08): the main flow for each permitted actor,
// AF-08a (404), AF-08b (409), AF-08c (403), the unsupported transition (400), the NFR-12 last-owner
// conflict (409), and the unauthenticated flow (401). Asserts response and database state, including
// that promoting a person to SystemAdmin really removes their join row.
[Collection(nameof(FunctionalCollection))]
public class PersonControllerUpdateTests(PostgresFixture db) : WebApiTest<Program>(EnvironmentType.Local)
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

    private async Task<Person> SeedUserAsync(Scope scope, string? email = null)
    {
        await using var context = db.CreateContext();
        var person = new Person
        {
            PublicId = Guid.NewGuid(),
            Name = "Member",
            Email = email ?? UniqueEmail("user"),
            RoleId = (long)Roles.User,
            EmailVerified = true
        };
        context.Persons.Add(person);
        await context.SaveChangesAsync();

        context.ScopeUsers.Add(new ScopeUser { ScopeId = scope.Id, PersonId = person.Id });
        await context.SaveChangesAsync();

        return person;
    }

    private async Task<Person> SeedScopeAdminAsync(Scope? ownedScope = null, string? email = null)
    {
        await using var context = db.CreateContext();
        var person = new Person
        {
            PublicId = Guid.NewGuid(),
            Name = "Admin",
            Email = email ?? UniqueEmail("admin"),
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

    private static UpdatePersonCommand Body(string name, string email, int? roleId = null) =>
        new() { Name = name, Email = email, RoleId = roleId };

    [FunctionalFact]
    public async Task GivenSystemAdmin_WhenPutPerson_ThenNameAndEmailAreUpdated()
    {
        // Given
        var scope = await SeedScopeAsync();
        var person = await SeedUserAsync(scope);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));
        var email = UniqueEmail("renamed");

        // When
        var response = await Gateway.PutAsync<DataOutput<UpdatePersonCommandOutput?>>(
            $"/api/persons/{person.PublicId}", Body("Renamed", email));

        // Then — response
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Renamed", response.Body?.Data?.Name);
        Assert.False(response.Body?.Data?.EmailVerified);

        // Then — database state: the email change cleared the verification flag
        await using var context = db.CreateContext();
        var stored = await context.Persons.AsNoTracking().FirstAsync(p => p.PublicId == person.PublicId);
        Assert.Equal("Renamed", stored.Name);
        Assert.Equal(email, stored.Email);
        Assert.False(stored.EmailVerified);
    }

    [FunctionalFact]
    public async Task GivenUserUpdatingSelf_WhenPutPerson_ThenPersonIsUpdated()
    {
        // Given a User authenticated as themselves
        var scope = await SeedScopeAsync();
        var person = await SeedUserAsync(scope);
        Authorize(TestTokens.For(person.PublicId, (int)Roles.User));

        // When
        var response = await Gateway.PutAsync<DataOutput<UpdatePersonCommandOutput?>>(
            $"/api/persons/{person.PublicId}", Body("Self Renamed", person.Email));

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Self Renamed", response.Body?.Data?.Name);
    }

    [FunctionalFact]
    public async Task GivenOwnerScopeAdmin_WhenPutScopeUser_ThenPersonIsUpdated()
    {
        // Given a ScopeAdmin who owns the User's scope
        var scope = await SeedScopeAsync();
        var owner = await SeedScopeAdminAsync(ownedScope: scope);
        var person = await SeedUserAsync(scope);
        Authorize(TestTokens.For(owner.PublicId, (int)Roles.ScopeAdmin));

        // When
        var response = await Gateway.PutAsync<DataOutput<UpdatePersonCommandOutput?>>(
            $"/api/persons/{person.PublicId}", Body("Owner Renamed", person.Email));

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Owner Renamed", response.Body?.Data?.Name);
    }

    [FunctionalFact]
    public async Task GivenNonOwningScopeAdmin_WhenPutScopeUser_ThenForbidden()
    {
        // Given a ScopeAdmin who does not own the User's scope
        var scope = await SeedScopeAsync();
        var outsider = await SeedScopeAdminAsync();
        var person = await SeedUserAsync(scope);
        Authorize(TestTokens.For(outsider.PublicId, (int)Roles.ScopeAdmin));

        // When
        var response = await Gateway.PutAsync<DataOutput<UpdatePersonCommandOutput?>>(
            $"/api/persons/{person.PublicId}", Body("Nope", person.Email));

        // Then
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenUserUpdatingAnotherPerson_WhenPutPerson_ThenForbidden()
    {
        // Given two Users of the same scope
        var scope = await SeedScopeAsync();
        var actor = await SeedUserAsync(scope);
        var target = await SeedUserAsync(scope);
        Authorize(TestTokens.For(actor.PublicId, (int)Roles.User));

        // When
        var response = await Gateway.PutAsync<DataOutput<UpdatePersonCommandOutput?>>(
            $"/api/persons/{target.PublicId}", Body("Nope", target.Email));

        // Then
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenScopeAdminChangingRole_WhenPutPerson_ThenForbidden()
    {
        // Given an owning ScopeAdmin attempting a role change (AF-08c)
        var scope = await SeedScopeAsync();
        var owner = await SeedScopeAdminAsync(ownedScope: scope);
        var person = await SeedUserAsync(scope);
        Authorize(TestTokens.For(owner.PublicId, (int)Roles.ScopeAdmin));

        // When
        var response = await Gateway.PutAsync<DataOutput<UpdatePersonCommandOutput?>>(
            $"/api/persons/{person.PublicId}",
            Body("Member", person.Email, (int)Roles.SystemAdmin));

        // Then
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenSystemAdminPromotingUserToSystemAdmin_WhenPutPerson_ThenScopeUserRowIsRemoved()
    {
        // Given a User in a scope (FR-PE-10: a System Admin belongs to no scope)
        var scope = await SeedScopeAsync();
        var person = await SeedUserAsync(scope);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.PutAsync<DataOutput<UpdatePersonCommandOutput?>>(
            $"/api/persons/{person.PublicId}",
            Body("Member", person.Email, (int)Roles.SystemAdmin));

        // Then — response
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal((int)Roles.SystemAdmin, response.Body?.Data?.Role);
        Assert.Null(response.Body?.Data?.ScopeId);

        // Then — database state: the join row is gone, the person remains
        await using var context = db.CreateContext();
        Assert.False(await context.ScopeUsers.AnyAsync(su => su.PersonId == person.Id));
        var stored = await context.Persons.AsNoTracking().FirstAsync(p => p.Id == person.Id);
        Assert.Equal((long)Roles.SystemAdmin, stored.RoleId);
    }

    [FunctionalFact]
    public async Task GivenSystemAdminPromotingUserToScopeAdmin_WhenPutPerson_ThenBadRequest()
    {
        // Given a transition that would need a target scope the request does not carry
        var scope = await SeedScopeAsync();
        var person = await SeedUserAsync(scope);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.PutAsync<DataOutput<UpdatePersonCommandOutput?>>(
            $"/api/persons/{person.PublicId}",
            Body("Member", person.Email, (int)Roles.ScopeAdmin));

        // Then
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenSoleOwner_WhenPromotingToSystemAdmin_ThenConflict()
    {
        // Given a scope whose only owner is the target (NFR-12)
        var scope = await SeedScopeAsync();
        var owner = await SeedScopeAdminAsync(ownedScope: scope);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.PutAsync<DataOutput<UpdatePersonCommandOutput?>>(
            $"/api/persons/{owner.PublicId}",
            Body("Admin", owner.Email, (int)Roles.SystemAdmin));

        // Then — refused, and the ownership row survives
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        await using var context = db.CreateContext();
        Assert.True(await context.ScopeOwners.AnyAsync(so => so.PersonId == owner.Id));
    }

    [FunctionalFact]
    public async Task GivenEmailTakenInScope_WhenPutPerson_ThenConflict()
    {
        // Given two Users in one scope (AF-08b)
        var scope = await SeedScopeAsync();
        var first = await SeedUserAsync(scope);
        var second = await SeedUserAsync(scope);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When the second takes the first's email
        var response = await Gateway.PutAsync<DataOutput<UpdatePersonCommandOutput?>>(
            $"/api/persons/{second.PublicId}", Body("Member", first.Email));

        // Then
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenUnknownPersonId_WhenPutPerson_ThenNotFound()
    {
        // Given (AF-08a)
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.PutAsync<DataOutput<UpdatePersonCommandOutput?>>(
            $"/api/persons/{Guid.NewGuid()}", Body("Ghost", UniqueEmail("ghost")));

        // Then
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenInvalidPayload_WhenPutPerson_ThenBadRequest()
    {
        // Given an empty name
        var scope = await SeedScopeAsync();
        var person = await SeedUserAsync(scope);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.PutAsync<DataOutput<UpdatePersonCommandOutput?>>(
            $"/api/persons/{person.PublicId}", Body(string.Empty, person.Email));

        // Then
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenNoToken_WhenPutPerson_ThenUnauthorized()
    {
        // Given a person but no bearer token on the gateway
        var scope = await SeedScopeAsync();
        var person = await SeedUserAsync(scope);

        // When
        var response = await Gateway.PutAsync<DataOutput<UpdatePersonCommandOutput?>>(
            $"/api/persons/{person.PublicId}", Body("Nope", person.Email));

        // Then
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
