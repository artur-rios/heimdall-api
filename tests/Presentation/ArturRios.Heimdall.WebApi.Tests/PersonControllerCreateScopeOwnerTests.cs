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

    [FunctionalFact]
    public async Task GivenOwnerScopeAdmin_WhenPostScopeOwners_ThenCoOwnerIsCreated()
    {
        // Given a ScopeAdmin who owns the scope
        var scope = await Seeds.ScopeAsync(db);
        var owner = await Seeds.PersonAsync(db, Roles.ScopeAdmin, new PersonOptions { EmailPrefix = "admin", Owns = [scope] });
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
        var scope = await Seeds.ScopeAsync(db);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));
        var command = Command();

        var response = await Gateway.PostAsync<DataOutput<CreatePersonCommandOutput?>>(
            $"/api/scopes/{scope.PublicId}/owners", command);

        // Then — the status alone would pass for a handler that answered 201 and wrote nothing, so
        // the row the endpoint exists to create is what is asserted: a ScopeAdmin person, and the
        // SCOPE_OWNER row that makes them an owner of this scope (UC-06 path c, FR-SC-12).
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        await using var context = db.CreateContext();

        var created = await context.Persons
            .Include(person => person.ScopeOwnerships)
            .FirstOrDefaultAsync(person => person.Email == command.Email);

        Assert.NotNull(created);
        Assert.Equal((long)Roles.ScopeAdmin, created.RoleId);
        Assert.Equal(created.PublicId, response.Body!.Data!.Id);
        Assert.Equal(scope.Id, Assert.Single(created.ScopeOwnerships).ScopeId);
    }

    [FunctionalFact]
    public async Task GivenScopeAdminNotOwner_WhenPostScopeOwners_ThenForbidden()
    {
        // AF-06e
        var scope = await Seeds.ScopeAsync(db);
        var admin = await Seeds.PersonAsync(db, Roles.ScopeAdmin, new PersonOptions { EmailPrefix = "admin" });
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
        var scope = await Seeds.ScopeAsync(db);
        var existing = await Seeds.PersonAsync(db, Roles.ScopeAdmin, new PersonOptions { EmailPrefix = "admin" });
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
        var scope = await Seeds.ScopeAsync(db);

        var response = await Gateway.PostAsync<DataOutput<CreatePersonCommandOutput?>>(
            $"/api/scopes/{scope.PublicId}/owners", Command());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
