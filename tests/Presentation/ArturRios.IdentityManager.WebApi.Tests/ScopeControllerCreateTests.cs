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

namespace ArturRios.IdentityManager.WebApi.Tests;

[Collection(nameof(FunctionalCollection))]
public class ScopeControllerCreateTests(PostgresFixture db) : WebApiTest<Program>(EnvironmentType.Local)
{
    private static string UniqueName() => $"scope-{Guid.NewGuid():N}";

    private async Task<Person> SeedPersonAsync(Roles role)
    {
        await using var context = db.CreateContext();

        var person = new Person
        {
            PublicId = Guid.NewGuid(),
            Name = "Owner",
            Email = $"person-{Guid.NewGuid():N}@test.local",
            RoleId = (long)role,
            EmailVerified = true
        };

        context.Persons.Add(person);
        await context.SaveChangesAsync();

        return person;
    }

    private async Task<Scope> SeedScopeAsync(string name)
    {
        await using var context = db.CreateContext();

        var scope = new Scope { PublicId = Guid.NewGuid(), Name = name };

        context.Scopes.Add(scope);
        await context.SaveChangesAsync();

        return scope;
    }

    [FunctionalFact]
    public async Task GivenSystemAdminAndValidOwner_WhenPostScope_ThenScopeIsCreated()
    {
        // Given
        var owner = await SeedPersonAsync(Roles.ScopeAdmin);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));
        var name = UniqueName();

        // When
        var response = await Gateway.PostAsync<DataOutput<CreateScopeCommandOutput?>>(
            "/api/scopes",
            new CreateScopeCommand { Name = name, Description = "New", OwnerIds = [owner.PublicId] });

        // Then — response
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(name, response.Body?.Data?.Name);
        Assert.Equal([owner.PublicId], response.Body?.Data?.OwnerIds);

        // Then — database state: the scope and one SCOPE_OWNER row for the owner
        await using var context = db.CreateContext();
        var persisted = await context.Scopes.Include(x => x.Owners).AsNoTracking()
            .FirstAsync(x => x.Name == name);
        Assert.Equal(owner.Id, Assert.Single(persisted.Owners).PersonId);
    }

    [FunctionalFact]
    public async Task GivenDuplicateName_WhenPostScope_ThenConflict()
    {
        // Given an existing scope and a valid owner
        var existing = await SeedScopeAsync(UniqueName());
        var owner = await SeedPersonAsync(Roles.ScopeAdmin);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.PostAsync<DataOutput<CreateScopeCommandOutput?>>(
            "/api/scopes",
            new CreateScopeCommand { Name = existing.Name, OwnerIds = [owner.PublicId] });

        // Then
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenDuplicateNameDifferentCase_WhenPostScope_ThenConflict()
    {
        // Given an existing scope and a valid owner; the new name differs only by case (name
        // uniqueness is case-insensitive)
        var existing = await SeedScopeAsync(UniqueName());
        var owner = await SeedPersonAsync(Roles.ScopeAdmin);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.PostAsync<DataOutput<CreateScopeCommandOutput?>>(
            "/api/scopes",
            new CreateScopeCommand { Name = existing.Name.ToUpperInvariant(), OwnerIds = [owner.PublicId] });

        // Then
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenNoOwner_WhenPostScope_ThenBadRequest()
    {
        // Given
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.PostAsync<DataOutput<CreateScopeCommandOutput?>>(
            "/api/scopes",
            new CreateScopeCommand { Name = UniqueName(), OwnerIds = [] });

        // Then
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenOwnerNotScopeAdmin_WhenPostScope_ThenBadRequest()
    {
        // Given an owner that is a plain User, not a ScopeAdmin (AF-01d)
        var owner = await SeedPersonAsync(Roles.User);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.PostAsync<DataOutput<CreateScopeCommandOutput?>>(
            "/api/scopes",
            new CreateScopeCommand { Name = UniqueName(), OwnerIds = [owner.PublicId] });

        // Then
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenNonSystemAdmin_WhenPostScope_ThenForbidden()
    {
        // Given a valid owner but a non-System-Admin caller (AF-01c)
        var owner = await SeedPersonAsync(Roles.ScopeAdmin);
        Authorize(TestTokens.ForRole((int)Roles.User));

        // When
        var response = await Gateway.PostAsync<DataOutput<CreateScopeCommandOutput?>>(
            "/api/scopes",
            new CreateScopeCommand { Name = UniqueName(), OwnerIds = [owner.PublicId] });

        // Then
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenNoToken_WhenPostScope_ThenUnauthorized()
    {
        // Given a valid owner but no bearer token on the gateway
        var owner = await SeedPersonAsync(Roles.ScopeAdmin);

        // When
        var response = await Gateway.PostAsync<DataOutput<CreateScopeCommandOutput?>>(
            "/api/scopes",
            new CreateScopeCommand { Name = UniqueName(), OwnerIds = [owner.PublicId] });

        // Then
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
