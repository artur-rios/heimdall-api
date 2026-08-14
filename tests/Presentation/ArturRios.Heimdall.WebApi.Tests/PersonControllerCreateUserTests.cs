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
public class PersonControllerCreateUserTests(PostgresFixture db) : WebApiTest<Program>(EnvironmentType.Local)
{
    private static CreateUserCommand Command() => new()
    {
        Name = "User", Email = $"user-{Guid.NewGuid():N}@test.local", Password = "Str0ngPass!"
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
    public async Task GivenSystemAdmin_WhenPostScopePersons_ThenUserIsCreated()
    {
        // Given
        var scope = await SeedScopeAsync();
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));
        var command = Command();

        // When
        var response = await Gateway.PostAsync<DataOutput<CreatePersonCommandOutput?>>(
            $"/api/scopes/{scope.PublicId}/persons", command);

        // Then — response
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(scope.PublicId, response.Body?.Data?.ScopeId);

        // Then — a User with a SCOPE_USER row and a verification token
        await using var context = db.CreateContext();
        var person = await context.Persons.AsNoTracking().FirstAsync(p => p.Email == command.Email);
        Assert.Equal((long)Roles.User, person.RoleId);
        Assert.True(await context.ScopeUsers.AnyAsync(su => su.PersonId == person.Id && su.ScopeId == scope.Id));
        Assert.True(await context.EmailVerificationTokens.AnyAsync(t => t.PersonId == person.Id));
    }

    [FunctionalFact]
    public async Task GivenOwnerScopeAdmin_WhenPostScopePersons_ThenUserIsCreated()
    {
        // Given a ScopeAdmin who owns the scope, authenticated with their own person id
        var scope = await SeedScopeAsync();
        var owner = await SeedScopeAdminAsync(ownedScope: scope);
        Authorize(TestTokens.For(owner.PublicId, (int)Roles.ScopeAdmin));
        var command = Command();

        // When
        var response = await Gateway.PostAsync<DataOutput<CreatePersonCommandOutput?>>(
            $"/api/scopes/{scope.PublicId}/persons", command);

        // Then — the row, not just the status. A handler that answered 201 and wrote nothing would
        // have passed on the status alone, and this test's name is a claim about what was created.
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        await using var context = db.CreateContext();

        var created = await context.Persons.AsNoTracking()
            .FirstOrDefaultAsync(person => person.Email == command.Email);

        Assert.NotNull(created);
        Assert.Equal((long)Roles.User, created.RoleId);
        Assert.True(await context.ScopeUsers
            .AnyAsync(membership => membership.PersonId == created.Id && membership.ScopeId == scope.Id));
    }

    [FunctionalFact]
    public async Task GivenScopeAdminNotOwner_WhenPostScopePersons_ThenForbidden()
    {
        // Given a ScopeAdmin who does NOT own the scope (AF-06e)
        var scope = await SeedScopeAsync();
        var admin = await SeedScopeAdminAsync();
        Authorize(TestTokens.For(admin.PublicId, (int)Roles.ScopeAdmin));

        // When
        var response = await Gateway.PostAsync<DataOutput<CreatePersonCommandOutput?>>(
            $"/api/scopes/{scope.PublicId}/persons", Command());

        // Then
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenMissingScope_WhenPostScopePersons_ThenNotFound()
    {
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        var response = await Gateway.PostAsync<DataOutput<CreatePersonCommandOutput?>>(
            $"/api/scopes/{Guid.NewGuid()}/persons", Command());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenDuplicateEmailInScope_WhenPostScopePersons_ThenConflict()
    {
        // Given a scope where the email is already taken by a User
        var scope = await SeedScopeAsync();
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));
        var command = Command();
        var first = await Gateway.PostAsync<DataOutput<CreatePersonCommandOutput?>>(
            $"/api/scopes/{scope.PublicId}/persons", command);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        // When posting the same email again
        var response = await Gateway.PostAsync<DataOutput<CreatePersonCommandOutput?>>(
            $"/api/scopes/{scope.PublicId}/persons", command);

        // Then
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenDuplicateEmailInScopeDifferentCase_WhenPostScopePersons_ThenConflict()
    {
        // Given a scope where the email is already taken by a User (AF-06a is case-insensitive)
        var scope = await SeedScopeAsync();
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));
        var command = Command();
        var first = await Gateway.PostAsync<DataOutput<CreatePersonCommandOutput?>>(
            $"/api/scopes/{scope.PublicId}/persons", command);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        // When posting the same email in a different case
        var duplicate = new CreateUserCommand
        {
            Name = "User", Email = command.Email.ToUpperInvariant(), Password = "Str0ngPass!"
        };
        var response = await Gateway.PostAsync<DataOutput<CreatePersonCommandOutput?>>(
            $"/api/scopes/{scope.PublicId}/persons", duplicate);

        // Then
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenPlainUserCaller_WhenPostScopePersons_ThenForbidden()
    {
        var scope = await SeedScopeAsync();
        Authorize(TestTokens.ForRole((int)Roles.User));

        var response = await Gateway.PostAsync<DataOutput<CreatePersonCommandOutput?>>(
            $"/api/scopes/{scope.PublicId}/persons", Command());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenNoToken_WhenPostScopePersons_ThenUnauthorized()
    {
        var scope = await SeedScopeAsync();

        var response = await Gateway.PostAsync<DataOutput<CreatePersonCommandOutput?>>(
            $"/api/scopes/{scope.PublicId}/persons", Command());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
