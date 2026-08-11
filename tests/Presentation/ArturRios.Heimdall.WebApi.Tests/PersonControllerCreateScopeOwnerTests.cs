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

[Collection(nameof(FunctionalCollection))]
public class PersonControllerCreateScopeOwnerTests(PostgresFixture db) : WebApiTest<Program>(EnvironmentType.Local)
{
    private static CreateScopeOwnerCommand Command() => new()
    {
        Name = "Owner", Email = $"owner-{Guid.NewGuid():N}@test.local", Password = "Str0ngPass!"
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

    [FunctionalFact]
    public async Task GivenOwnerScopeAdmin_WhenPostScopeOwners_ThenCoOwnerIsCreated()
    {
        // Given a ScopeAdmin who owns the scope
        var scope = await SeedScopeAsync();
        var owner = await SeedScopeAdminAsync(ownedScope: scope);
        Authorize(TestTokens.For(owner.PublicId, (int)Roles.ScopeAdmin));
        var command = Command();

        // When
        var response = await Gateway.PostAsync<DataOutput<CreatePersonCommandOutput?>>(
            $"/api/scopes/{scope.PublicId}/owners", command);

        // Then — response
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal((int)Roles.ScopeAdmin, response.Body?.Data?.Role);
        Assert.Equal(scope.PublicId, response.Body?.Data?.ScopeId);

        // Then — a ScopeAdmin with a SCOPE_OWNER row and a verification token
        await using var context = db.CreateContext();
        var person = await context.Persons.AsNoTracking().FirstAsync(p => p.Email == command.Email);
        Assert.Equal((long)Roles.ScopeAdmin, person.RoleId);
        Assert.True(await context.ScopeOwners.AnyAsync(so => so.PersonId == person.Id && so.ScopeId == scope.Id));
        Assert.True(await context.EmailVerificationTokens.AnyAsync(t => t.PersonId == person.Id));
    }

    [FunctionalFact]
    public async Task GivenSystemAdmin_WhenPostScopeOwners_ThenCoOwnerIsCreated()
    {
        var scope = await SeedScopeAsync();
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        var response = await Gateway.PostAsync<DataOutput<CreatePersonCommandOutput?>>(
            $"/api/scopes/{scope.PublicId}/owners", Command());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenScopeAdminNotOwner_WhenPostScopeOwners_ThenForbidden()
    {
        // AF-06e
        var scope = await SeedScopeAsync();
        var admin = await SeedScopeAdminAsync();
        Authorize(TestTokens.For(admin.PublicId, (int)Roles.ScopeAdmin));

        var response = await Gateway.PostAsync<DataOutput<CreatePersonCommandOutput?>>(
            $"/api/scopes/{scope.PublicId}/owners", Command());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenMissingScope_WhenPostScopeOwners_ThenNotFound()
    {
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        var response = await Gateway.PostAsync<DataOutput<CreatePersonCommandOutput?>>(
            $"/api/scopes/{Guid.NewGuid()}/owners", Command());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenDuplicateAdminEmail_WhenPostScopeOwners_ThenConflict()
    {
        var scope = await SeedScopeAsync();
        var existing = await SeedScopeAdminAsync();
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));
        var command = Command();
        command.Email = existing.Email;

        var response = await Gateway.PostAsync<DataOutput<CreatePersonCommandOutput?>>(
            $"/api/scopes/{scope.PublicId}/owners", command);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenNoToken_WhenPostScopeOwners_ThenUnauthorized()
    {
        var scope = await SeedScopeAsync();

        var response = await Gateway.PostAsync<DataOutput<CreatePersonCommandOutput?>>(
            $"/api/scopes/{scope.PublicId}/owners", Command());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
