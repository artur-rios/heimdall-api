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
public class PersonControllerCreateAdminTests(PostgresFixture db) : WebApiTest<Program>(EnvironmentType.Local)
{
    private static string UniqueEmail() => $"admin-{Guid.NewGuid():N}@test.local";

    private static CreateAdminCommand Command(string email, int role) => new()
    {
        Name = "Admin", Email = email, Password = "Str0ngPass!", Role = role
    };

    private async Task<Person> SeedAdminAsync(string email, Roles role)
    {
        await using var context = db.CreateContext();
        var person = new Person
        {
            PublicId = Guid.NewGuid(), Name = "Existing", Email = email,
            RoleId = (long)role, EmailVerified = true
        };
        context.Persons.Add(person);
        await context.SaveChangesAsync();
        return person;
    }

    [FunctionalFact]
    public async Task GivenSystemAdminAndValidScopeAdmin_WhenPostPersons_ThenCreated()
    {
        // Given
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));
        var email = UniqueEmail();

        // When
        var response = await Gateway.PostAsync<DataOutput<CreatePersonCommandOutput?>>(
            "/api/persons", Command(email, (int)Roles.ScopeAdmin));

        // Then — response
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(email, response.Body?.Data?.Email);
        Assert.Null(response.Body?.Data?.ScopeId);

        // Then — database state: person with RoleId=ScopeAdmin, EmailVerified=false, a token, no join rows
        await using var context = db.CreateContext();
        var person = await context.Persons.AsNoTracking().FirstAsync(p => p.Email == email);
        Assert.Equal((long)Roles.ScopeAdmin, person.RoleId);
        Assert.False(person.EmailVerified);
        Assert.NotEmpty(person.PasswordHash);
        Assert.True(await context.EmailVerificationTokens.AnyAsync(t => t.PersonId == person.Id));
        Assert.False(await context.ScopeUsers.AnyAsync(su => su.PersonId == person.Id));
        Assert.False(await context.ScopeOwners.AnyAsync(so => so.PersonId == person.Id));
    }

    [FunctionalFact]
    public async Task GivenDuplicateAdminEmail_WhenPostPersons_ThenConflict()
    {
        var existing = await SeedAdminAsync(UniqueEmail(), Roles.ScopeAdmin);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        var response = await Gateway.PostAsync<DataOutput<CreatePersonCommandOutput?>>(
            "/api/persons", Command(existing.Email, (int)Roles.SystemAdmin));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenDuplicateAdminEmailDifferentCase_WhenPostPersons_ThenConflict()
    {
        // AF-06a is case-insensitive: an admin email differing only by case is a duplicate.
        var existing = await SeedAdminAsync(UniqueEmail(), Roles.ScopeAdmin);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        var response = await Gateway.PostAsync<DataOutput<CreatePersonCommandOutput?>>(
            "/api/persons", Command(existing.Email.ToUpperInvariant(), (int)Roles.SystemAdmin));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenInvalidRole_WhenPostPersons_ThenBadRequest()
    {
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        var response = await Gateway.PostAsync<DataOutput<CreatePersonCommandOutput?>>(
            "/api/persons", Command(UniqueEmail(), (int)Roles.User));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenScopeAdminCaller_WhenPostPersons_ThenForbidden()
    {
        // AF-06c: only a System Admin may use path b.
        Authorize(TestTokens.ForRole((int)Roles.ScopeAdmin));

        var response = await Gateway.PostAsync<DataOutput<CreatePersonCommandOutput?>>(
            "/api/persons", Command(UniqueEmail(), (int)Roles.ScopeAdmin));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenNoToken_WhenPostPersons_ThenUnauthorized()
    {
        var response = await Gateway.PostAsync<DataOutput<CreatePersonCommandOutput?>>(
            "/api/persons", Command(UniqueEmail(), (int)Roles.ScopeAdmin));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
