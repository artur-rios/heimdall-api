using System.Net;
using ArturRios.Configuration.Enums;
using ArturRios.IdentityManager.Command.Input;
using ArturRios.IdentityManager.Command.Output;
using ArturRios.IdentityManager.Domain.Entities;
using ArturRios.IdentityManager.Domain.Enums;
using ArturRios.IdentityManager.WebApi.Tests.Support;
using ArturRios.Output;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Functional;
using Microsoft.EntityFrameworkCore;
using Application = ArturRios.IdentityManager.Domain.Entities.Application;

namespace ArturRios.IdentityManager.WebApi.Tests;

// Functional tests for PUT /api/scopes/{scopeId}/applications/{id} (UC-18, FR-AP-06): the main flow
// for a System Admin and for the owning Scope Admin, the owner transfer the specification allows,
// AF-18a (unknown id, wrong scope, logically deleted), AF-18b (a new owner FR-AP-03 refuses), AF-18c
// (a co-owner of the scope who does not own the application), and the framework-level flows (403 for
// a User, 401 unauthenticated).
[Collection(nameof(FunctionalCollection))]
public class ApplicationControllerUpdateTests(PostgresFixture db) : WebApiTest<Program>(EnvironmentType.Local)
{
    // A fixed, obviously-not-now timestamp, so "UpdatedAt was stamped" is a meaningful assertion.
    private static readonly DateTime Stamp = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static string Route(Guid scopeId, Guid id) => $"/api/scopes/{scopeId}/applications/{id}";

    private static UpdateApplicationCommand Body(Guid ownerId, string? name = null) => new()
    {
        Name = name ?? $"app-{Guid.NewGuid():N}", OwnerId = ownerId
    };

    private async Task<Scope> SeedScopeAsync()
    {
        await using var context = db.CreateContext();
        var scope = new Scope { PublicId = Guid.NewGuid(), Name = $"scope-{Guid.NewGuid():N}" };
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
    public async Task GivenSystemAdmin_WhenPutApplication_ThenOkAndRowIsUpdated()
    {
        // Given an application a System Admin does not own
        var scope = await SeedScopeAsync();
        var owner = await SeedScopeAdminAsync(ownedScope: scope);
        var application = await SeedApplicationAsync(scope, owner);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));
        var body = Body(owner.PublicId, "Renamed");

        // When
        var response = await Gateway.PutAsync<DataOutput<UpdateApplicationCommandOutput?>>(
            Route(scope.PublicId, application.PublicId), body);

        // Then — response carries public identifiers only
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(application.PublicId, response.Body?.Data?.Id);
        Assert.Equal("Renamed", response.Body?.Data?.Name);
        Assert.Equal(scope.PublicId, response.Body?.Data?.ScopeId);
        Assert.Equal(owner.PublicId, response.Body?.Data?.OwnerId);

        // Then — database state: renamed, still owned by the same person, UpdatedAt stamped
        var stored = await StoredAsync(application.PublicId);
        Assert.Equal("Renamed", stored.Name);
        Assert.Equal(owner.Id, stored.OwnerId);
        Assert.NotEqual(Stamp, stored.UpdatedAt);
    }

    [FunctionalFact]
    public async Task GivenOwningScopeAdmin_WhenPutApplication_ThenOkAndRowIsUpdated()
    {
        // Given the Scope Admin who owns the application (UC-18 step 3)
        var scope = await SeedScopeAsync();
        var owner = await SeedScopeAdminAsync(ownedScope: scope);
        var application = await SeedApplicationAsync(scope, owner);
        Authorize(TestTokens.For(owner.PublicId, (int)Roles.ScopeAdmin));

        // When
        var response = await Gateway.PutAsync<DataOutput<UpdateApplicationCommandOutput?>>(
            Route(scope.PublicId, application.PublicId), Body(owner.PublicId, "Renamed by owner"));

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Renamed by owner", (await StoredAsync(application.PublicId)).Name);
    }

    [FunctionalFact]
    public async Task GivenOwningScopeAdminTransferringToACoOwner_WhenPutApplication_ThenOkAndOwnerRowMoves()
    {
        // Given the owning Scope Admin naming a co-owner of the scope: UC-18 defines no equivalent of
        // UC-16's AF-16c, so giving away an application one owns is allowed
        var scope = await SeedScopeAsync();
        var owner = await SeedScopeAdminAsync(ownedScope: scope);
        var coOwner = await SeedScopeAdminAsync(ownedScope: scope);
        var application = await SeedApplicationAsync(scope, owner);
        Authorize(TestTokens.For(owner.PublicId, (int)Roles.ScopeAdmin));

        // When
        var response = await Gateway.PutAsync<DataOutput<UpdateApplicationCommandOutput?>>(
            Route(scope.PublicId, application.PublicId), Body(coOwner.PublicId, "Transferred"));

        // Then — response and row both name the co-owner
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(coOwner.PublicId, response.Body?.Data?.OwnerId);

        var stored = await StoredAsync(application.PublicId);
        Assert.Equal(coOwner.Id, stored.OwnerId);
        Assert.Equal("Transferred", stored.Name);
    }

    [FunctionalFact]
    public async Task GivenScopeAdminWhoOwnsTheScopeButNotTheApplication_WhenPutApplication_ThenForbidden()
    {
        // Given a co-owner of the scope: owning the scope is not grounds to modify another owner's
        // application (AF-18c)
        var scope = await SeedScopeAsync();
        var owner = await SeedScopeAdminAsync(ownedScope: scope);
        var coOwner = await SeedScopeAdminAsync(ownedScope: scope);
        var application = await SeedApplicationAsync(scope, owner);
        Authorize(TestTokens.For(coOwner.PublicId, (int)Roles.ScopeAdmin));

        // When
        var response = await Gateway.PutAsync<DataOutput<UpdateApplicationCommandOutput?>>(
            Route(scope.PublicId, application.PublicId), Body(coOwner.PublicId, "Hijacked"));

        // Then — refused, and nothing moved
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        var stored = await StoredAsync(application.PublicId);
        Assert.Equal(application.Name, stored.Name);
        Assert.Equal(owner.Id, stored.OwnerId);
        Assert.Equal(Stamp, stored.UpdatedAt);
    }

    [FunctionalFact]
    public async Task GivenUserRole_WhenPutApplication_ThenForbidden()
    {
        // Given a caller holding the User role: FR-AP-03 lets them own no application, so the
        // endpoint's [RoleRequirement] refuses them
        var scope = await SeedScopeAsync();
        var owner = await SeedScopeAdminAsync(ownedScope: scope);
        var application = await SeedApplicationAsync(scope, owner);
        var caller = await SeedUserAsync(scope);
        Authorize(TestTokens.For(caller.PublicId, (int)Roles.User));

        // When
        var response = await Gateway.PutAsync<DataOutput<UpdateApplicationCommandOutput?>>(
            Route(scope.PublicId, application.PublicId), Body(owner.PublicId, "Renamed"));

        // Then
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(application.Name, (await StoredAsync(application.PublicId)).Name);
    }

    [FunctionalFact]
    public async Task GivenUnknownApplication_WhenPutApplication_ThenNotFound()
    {
        // Given an application id nobody holds (AF-18a)
        var scope = await SeedScopeAsync();
        var owner = await SeedScopeAdminAsync(ownedScope: scope);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.PutAsync<DataOutput<UpdateApplicationCommandOutput?>>(
            Route(scope.PublicId, Guid.NewGuid()), Body(owner.PublicId));

        // Then
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenApplicationOfAnotherScope_WhenPutApplication_ThenNotFound()
    {
        // Given the application exists, but under a different scope than the path addresses (AF-18a)
        var scope = await SeedScopeAsync();
        var otherScope = await SeedScopeAsync();
        var owner = await SeedScopeAdminAsync(ownedScope: otherScope);
        var application = await SeedApplicationAsync(otherScope, owner);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.PutAsync<DataOutput<UpdateApplicationCommandOutput?>>(
            Route(scope.PublicId, application.PublicId), Body(owner.PublicId, "Renamed"));

        // Then — refused, and the row is untouched
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(application.Name, (await StoredAsync(application.PublicId)).Name);
    }

    [FunctionalFact]
    public async Task GivenLogicallyDeletedApplication_WhenPutApplication_ThenNotFound()
    {
        // Given a logically deleted application: the precondition excludes it (AF-18a)
        var scope = await SeedScopeAsync();
        var owner = await SeedScopeAdminAsync(ownedScope: scope);
        var application = await SeedApplicationAsync(scope, owner, isDeleted: true);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.PutAsync<DataOutput<UpdateApplicationCommandOutput?>>(
            Route(scope.PublicId, application.PublicId), Body(owner.PublicId, "Renamed"));

        // Then
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(application.Name, (await StoredAsync(application.PublicId)).Name);
    }

    [FunctionalFact]
    public async Task GivenNewOwnerWithUserRole_WhenPutApplication_ThenBadRequest()
    {
        // Given a User of the scope as the proposed new owner: FR-AP-03 restricts ownership to a
        // ScopeAdmin who owns the scope (AF-18b)
        var scope = await SeedScopeAsync();
        var owner = await SeedScopeAdminAsync(ownedScope: scope);
        var newOwner = await SeedUserAsync(scope);
        var application = await SeedApplicationAsync(scope, owner);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.PutAsync<DataOutput<UpdateApplicationCommandOutput?>>(
            Route(scope.PublicId, application.PublicId), Body(newOwner.PublicId, "Renamed"));

        // Then — refused, and the owner did not move
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(owner.Id, (await StoredAsync(application.PublicId)).OwnerId);
    }

    [FunctionalFact]
    public async Task GivenNewOwnerOfADifferentScope_WhenPutApplication_ThenBadRequest()
    {
        // Given a ScopeAdmin who owns another scope entirely (AF-18b)
        var scope = await SeedScopeAsync();
        var otherScope = await SeedScopeAsync();
        var owner = await SeedScopeAdminAsync(ownedScope: scope);
        var stranger = await SeedScopeAdminAsync(ownedScope: otherScope);
        var application = await SeedApplicationAsync(scope, owner);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.PutAsync<DataOutput<UpdateApplicationCommandOutput?>>(
            Route(scope.PublicId, application.PublicId), Body(stranger.PublicId, "Renamed"));

        // Then
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(owner.Id, (await StoredAsync(application.PublicId)).OwnerId);
    }

    [FunctionalFact]
    public async Task GivenUnknownNewOwner_WhenPutApplication_ThenBadRequest()
    {
        // Given an owner id nobody holds (AF-18b)
        var scope = await SeedScopeAsync();
        var owner = await SeedScopeAdminAsync(ownedScope: scope);
        var application = await SeedApplicationAsync(scope, owner);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.PutAsync<DataOutput<UpdateApplicationCommandOutput?>>(
            Route(scope.PublicId, application.PublicId), Body(Guid.NewGuid(), "Renamed"));

        // Then
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenEmptyName_WhenPutApplication_ThenBadRequest()
    {
        // Given a body with no name (UC-18 step 2)
        var scope = await SeedScopeAsync();
        var owner = await SeedScopeAdminAsync(ownedScope: scope);
        var application = await SeedApplicationAsync(scope, owner);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.PutAsync<DataOutput<UpdateApplicationCommandOutput?>>(
            Route(scope.PublicId, application.PublicId), Body(owner.PublicId, string.Empty));

        // Then
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(application.Name, (await StoredAsync(application.PublicId)).Name);
    }

    [FunctionalFact]
    public async Task GivenForgedActingRoleInBody_WhenPutApplication_ThenItIsIgnored()
    {
        // Given a Scope Admin claiming SystemAdmin in the body: ApplyActor runs after model binding
        // and overwrites both acting fields from the token, so the AF-18c refusal still stands
        var scope = await SeedScopeAsync();
        var owner = await SeedScopeAdminAsync(ownedScope: scope);
        var coOwner = await SeedScopeAdminAsync(ownedScope: scope);
        var application = await SeedApplicationAsync(scope, owner);
        Authorize(TestTokens.For(coOwner.PublicId, (int)Roles.ScopeAdmin));
        var body = Body(coOwner.PublicId, "Hijacked");
        body.ActingRole = (int)Roles.SystemAdmin;
        body.ActingPersonId = owner.PublicId;

        // When
        var response = await Gateway.PutAsync<DataOutput<UpdateApplicationCommandOutput?>>(
            Route(scope.PublicId, application.PublicId), body);

        // Then
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(owner.Id, (await StoredAsync(application.PublicId)).OwnerId);
    }

    [FunctionalFact]
    public async Task GivenNoToken_WhenPutApplication_ThenUnauthorized()
    {
        // Given no bearer token (precondition: the actor is authenticated)
        var scope = await SeedScopeAsync();

        // When
        var response = await Gateway.PutAsync<DataOutput<UpdateApplicationCommandOutput?>>(
            Route(scope.PublicId, Guid.NewGuid()), Body(Guid.NewGuid()));

        // Then
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
