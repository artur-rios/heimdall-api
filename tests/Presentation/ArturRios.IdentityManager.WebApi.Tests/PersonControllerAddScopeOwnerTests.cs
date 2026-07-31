using System.Net;
using ArturRios.Configuration.Enums;
using ArturRios.IdentityManager.Command.Output;
using ArturRios.IdentityManager.Domain.Entities;
using ArturRios.IdentityManager.Domain.Enums;
using ArturRios.IdentityManager.Shared.Messages;
using ArturRios.IdentityManager.WebApi.Tests.Support;
using ArturRios.Output;
using ArturRios.Util.Http;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Functional;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.IdentityManager.WebApi.Tests;

// Functional tests for POST /api/scopes/{scopeId}/owners/{personId} (UC-21, FR-SC-08/FR-SC-09): the
// main flow for a System Admin and for an existing owner, AF-21a (scope unknown or logically
// deleted), AF-21b (person unknown, logically deleted, or not a ScopeAdmin), AF-21c (a Scope Admin
// who owns a different scope), AF-21d (a repeated call is an idempotent 200 that does not duplicate
// the row), and the framework flows the actor list produces (403 for a User, 401 unauthenticated).
[Collection(nameof(FunctionalCollection))]
public class PersonControllerAddScopeOwnerTests(PostgresFixture db) : WebApiTest<Program>(EnvironmentType.Local)
{
    private static string Route(Guid scopeId, Guid personId) => $"/api/scopes/{scopeId}/owners/{personId}";

    /// <summary>
    ///     The request carries no body — both identifiers are route values — but
    ///     <c>HttpGateway.PostAsync</c> takes a payload, so an empty object is sent and the action,
    ///     which binds no body, ignores it.
    /// </summary>
    private Task<HttpOutput<DataOutput<AddScopeOwnerCommandOutput?>?>> AddOwnerAsync(Guid scopeId, Guid personId) =>
        Gateway.PostAsync<DataOutput<AddScopeOwnerCommandOutput?>>(Route(scopeId, personId), new { });

    private async Task<Scope> SeedScopeAsync(bool isDeleted = false)
    {
        await using var context = db.CreateContext();
        var scope = new Scope { PublicId = Guid.NewGuid(), Name = $"scope-{Guid.NewGuid():N}", IsDeleted = isDeleted };
        context.Scopes.Add(scope);
        await context.SaveChangesAsync();
        return scope;
    }

    private async Task<Person> SeedScopeAdminAsync(Scope? ownedScope = null, bool isDeleted = false)
    {
        await using var context = db.CreateContext();
        var person = new Person
        {
            PublicId = Guid.NewGuid(), Name = "Admin",
            Email = $"admin-{Guid.NewGuid():N}@test.local",
            RoleId = (long)Roles.ScopeAdmin, EmailVerified = true, IsDeleted = isDeleted
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

    private async Task<int> OwnershipCountAsync(Scope scope, Person person)
    {
        await using var context = db.CreateContext();
        return await context.ScopeOwners.CountAsync(so => so.ScopeId == scope.Id && so.PersonId == person.Id);
    }

    [FunctionalFact]
    public async Task GivenSystemAdmin_WhenPostScopeOwner_ThenCreatedAndRowExists()
    {
        // Given a scope and a ScopeAdmin who does not own it yet (UC-21 main flow)
        var scope = await SeedScopeAsync();
        var person = await SeedScopeAdminAsync();
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await AddOwnerAsync(scope.PublicId, person.PublicId);

        // Then — response
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(scope.PublicId, response.Body?.Data?.ScopeId);
        Assert.Equal(person.PublicId, response.Body?.Data?.PersonId);
        Assert.False(response.Body?.Data?.AlreadyOwner);
        Assert.Contains(PersonMessages.ScopeOwnerAddedSuccessfully, response.Body!.Messages);

        // Then — database state
        Assert.Equal(1, await OwnershipCountAsync(scope, person));
    }

    [FunctionalFact]
    public async Task GivenExistingOwner_WhenPostScopeOwner_ThenCreatedAndRowExists()
    {
        // Given a Scope Admin who already owns the scope adding a co-owner (FR-SC-09)
        var scope = await SeedScopeAsync();
        var actor = await SeedScopeAdminAsync(ownedScope: scope);
        var person = await SeedScopeAdminAsync();
        Authorize(TestTokens.For(actor.PublicId, (int)Roles.ScopeAdmin));

        // When
        var response = await AddOwnerAsync(scope.PublicId, person.PublicId);

        // Then — the scope now has both owners
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(1, await OwnershipCountAsync(scope, person));
        Assert.Equal(1, await OwnershipCountAsync(scope, actor));
    }

    [FunctionalFact]
    public async Task GivenScopeAdminOfAnotherScope_WhenPostScopeOwner_ThenForbidden()
    {
        // Given a Scope Admin who owns some other scope (AF-21c)
        var scope = await SeedScopeAsync();
        var otherScope = await SeedScopeAsync();
        var actor = await SeedScopeAdminAsync(ownedScope: otherScope);
        var person = await SeedScopeAdminAsync();
        Authorize(TestTokens.For(actor.PublicId, (int)Roles.ScopeAdmin));

        // When
        var response = await AddOwnerAsync(scope.PublicId, person.PublicId);

        // Then
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, await OwnershipCountAsync(scope, person));
    }

    [FunctionalFact]
    public async Task GivenUserRole_WhenPostScopeOwner_ThenForbidden()
    {
        // Given a caller holding the User role — refused by the attribute, before any handler runs
        var scope = await SeedScopeAsync();
        var person = await SeedScopeAdminAsync();
        var user = await SeedUserAsync(scope);
        Authorize(TestTokens.For(user.PublicId, (int)Roles.User));

        // When
        var response = await AddOwnerAsync(scope.PublicId, person.PublicId);

        // Then
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, await OwnershipCountAsync(scope, person));
    }

    [FunctionalFact]
    public async Task GivenUnknownScope_WhenPostScopeOwner_ThenNotFound()
    {
        // AF-21a
        var person = await SeedScopeAdminAsync();
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        var response = await AddOwnerAsync(Guid.NewGuid(), person.PublicId);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains(PersonMessages.ScopeNotFound, response.Body!.Errors);
    }

    [FunctionalFact]
    public async Task GivenLogicallyDeletedScope_WhenPostScopeOwner_ThenNotFound()
    {
        // AF-21a treats a logically deleted scope as absent
        var scope = await SeedScopeAsync(isDeleted: true);
        var person = await SeedScopeAdminAsync();
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        var response = await AddOwnerAsync(scope.PublicId, person.PublicId);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, await OwnershipCountAsync(scope, person));
    }

    [FunctionalFact]
    public async Task GivenUnknownPerson_WhenPostScopeOwner_ThenBadRequest()
    {
        // AF-21b
        var scope = await SeedScopeAsync();
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        var response = await AddOwnerAsync(scope.PublicId, Guid.NewGuid());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(PersonMessages.PersonNotValidScopeAdmin, response.Body!.Errors);
    }

    [FunctionalFact]
    public async Task GivenLogicallyDeletedPerson_WhenPostScopeOwner_ThenBadRequest()
    {
        // AF-21b — a deleted person can no longer authenticate, so the ownership would be unusable
        var scope = await SeedScopeAsync();
        var person = await SeedScopeAdminAsync(isDeleted: true);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        var response = await AddOwnerAsync(scope.PublicId, person.PublicId);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await OwnershipCountAsync(scope, person));
    }

    [FunctionalFact]
    public async Task GivenUserPerson_WhenPostScopeOwner_ThenBadRequest()
    {
        // AF-21b — only a ScopeAdmin may own a scope (FR-SC-08); promoting a User is UC-23
        var scope = await SeedScopeAsync();
        var person = await SeedUserAsync(scope);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        var response = await AddOwnerAsync(scope.PublicId, person.PublicId);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await OwnershipCountAsync(scope, person));
    }

    [FunctionalFact]
    public async Task GivenPersonAddedTwice_WhenPostScopeOwner_ThenSecondCallIsOkAndRowIsNotDuplicated()
    {
        // AF-21d: the repeat is an idempotent 200, distinct from the main flow's 201, and the
        // composite key is never written twice
        var scope = await SeedScopeAsync();
        var person = await SeedScopeAdminAsync();
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var first = await AddOwnerAsync(scope.PublicId, person.PublicId);
        var second = await AddOwnerAsync(scope.PublicId, person.PublicId);

        // Then
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.True(second.Body?.Data?.AlreadyOwner);
        Assert.Contains(PersonMessages.AlreadyScopeOwner, second.Body!.Messages);
        Assert.Equal(1, await OwnershipCountAsync(scope, person));
    }

    [FunctionalFact]
    public async Task GivenNoToken_WhenPostScopeOwner_ThenUnauthorized()
    {
        var scope = await SeedScopeAsync();
        var person = await SeedScopeAdminAsync();

        var response = await AddOwnerAsync(scope.PublicId, person.PublicId);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, await OwnershipCountAsync(scope, person));
    }
}
