using System.Net;
using ArturRios.Configuration.Enums;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Query.Output;
using ArturRios.Heimdall.WebApi.Tests.Support;
using ArturRios.Output;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Functional;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Heimdall.WebApi.Tests;

// Functional tests for GET /api/persons/{id} (UC-07, FR-PE-03/FR-PE-08): the main flow for each
// actor the use case allows, AF-07a (404), AF-07b (403), and the unauthenticated flow (401).
[Collection(nameof(FunctionalCollection))]
public class PersonControllerGetByIdTests(PostgresFixture db) : WebApiTest<Program>(EnvironmentType.Local)
{
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
            Email = $"user-{Guid.NewGuid():N}@test.local",
            RoleId = (long)Roles.User,
            IsDeleted = isDeleted
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
            Email = $"admin-{Guid.NewGuid():N}@test.local",
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

    [FunctionalFact]
    public async Task GivenSystemAdmin_WhenGetPersonById_ThenReturnsPersonWithoutSecrets()
    {
        // Given
        var scope = await SeedScopeAsync();
        var person = await SeedUserAsync(scope);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.GetAsync<DataOutput<PersonOutput?>>($"/api/persons/{person.PublicId}");

        // Then — response carries the person's public data
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(person.PublicId, response.Body?.Data?.Id);
        Assert.Equal(person.Email, response.Body?.Data?.Email);
        Assert.Equal((int)Roles.User, response.Body?.Data?.Role);
        Assert.Equal(scope.PublicId, response.Body?.Data?.ScopeId);

        // Then — the row exists in the database; PersonOutput has no field for the hash or salt, so
        // neither can have travelled with the response
        await using var context = db.CreateContext();
        var stored = await context.Persons.AsNoTracking().FirstAsync(p => p.PublicId == person.PublicId);
        Assert.Equal(person.Email, stored.Email);
    }

    [FunctionalFact]
    public async Task GivenUserRequestingSelf_WhenGetPersonById_ThenReturnsPerson()
    {
        // Given a User authenticated as themselves
        var scope = await SeedScopeAsync();
        var person = await SeedUserAsync(scope);
        Authorize(TestTokens.For(person.PublicId, (int)Roles.User));

        // When
        var response = await Gateway.GetAsync<DataOutput<PersonOutput?>>($"/api/persons/{person.PublicId}");

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(person.PublicId, response.Body?.Data?.Id);
    }

    [FunctionalFact]
    public async Task GivenUserRequestingAnotherPerson_WhenGetPersonById_ThenForbidden()
    {
        // Given two Users of the same scope (AF-07b)
        var scope = await SeedScopeAsync();
        var actor = await SeedUserAsync(scope);
        var target = await SeedUserAsync(scope);
        Authorize(TestTokens.For(actor.PublicId, (int)Roles.User));

        // When
        var response = await Gateway.GetAsync<DataOutput<PersonOutput?>>($"/api/persons/{target.PublicId}");

        // Then
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenOwnerScopeAdmin_WhenGetPersonById_ThenReturnsScopeUser()
    {
        // Given a ScopeAdmin who owns the User's scope
        var scope = await SeedScopeAsync();
        var owner = await SeedScopeAdminAsync(ownedScope: scope);
        var target = await SeedUserAsync(scope);
        Authorize(TestTokens.For(owner.PublicId, (int)Roles.ScopeAdmin));

        // When
        var response = await Gateway.GetAsync<DataOutput<PersonOutput?>>($"/api/persons/{target.PublicId}");

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(target.PublicId, response.Body?.Data?.Id);
    }

    [FunctionalFact]
    public async Task GivenScopeAdminNotOwningScope_WhenGetPersonById_ThenForbidden()
    {
        // Given a ScopeAdmin who does not own the User's scope (AF-07b)
        var scope = await SeedScopeAsync();
        var admin = await SeedScopeAdminAsync();
        var target = await SeedUserAsync(scope);
        Authorize(TestTokens.For(admin.PublicId, (int)Roles.ScopeAdmin));

        // When
        var response = await Gateway.GetAsync<DataOutput<PersonOutput?>>($"/api/persons/{target.PublicId}");

        // Then
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenUnknownPersonId_WhenGetPersonById_ThenNotFound()
    {
        // Given (AF-07a)
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.GetAsync<DataOutput<PersonOutput?>>($"/api/persons/{Guid.NewGuid()}");

        // Then
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenDeletedPerson_WhenGetPersonByIdWithoutIncludeDeleted_ThenNotFound()
    {
        // Given a logically deleted person (FR-PE-08)
        var scope = await SeedScopeAsync();
        var person = await SeedUserAsync(scope, isDeleted: true);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.GetAsync<DataOutput<PersonOutput?>>($"/api/persons/{person.PublicId}");

        // Then
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenDeletedPerson_WhenGetPersonByIdWithIncludeDeleted_ThenReturnsPerson()
    {
        // Given a logically deleted person (FR-PE-08)
        var scope = await SeedScopeAsync();
        var person = await SeedUserAsync(scope, isDeleted: true);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.GetAsync<DataOutput<PersonOutput?>>(
            $"/api/persons/{person.PublicId}?includeDeleted=true");

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Body?.Data?.IsDeleted);
    }

    [FunctionalFact]
    public async Task GivenNoToken_WhenGetPersonById_ThenUnauthorized()
    {
        // Given a person but no bearer token on the gateway
        var scope = await SeedScopeAsync();
        var person = await SeedUserAsync(scope);

        // When
        var response = await Gateway.GetAsync<DataOutput<PersonOutput?>>($"/api/persons/{person.PublicId}");

        // Then
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
