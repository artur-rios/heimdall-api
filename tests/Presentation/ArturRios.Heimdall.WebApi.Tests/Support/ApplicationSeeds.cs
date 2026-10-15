using ArturRios.Heimdall.Command.Services;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Application = ArturRios.Heimdall.Domain.Entities.Application;

namespace ArturRios.Heimdall.WebApi.Tests.Support;

/// <summary>
///     Seeds the scopes, owners, applications and credentials UC-53…UC-57's functional tests share,
///     straight through the database, so a test's Given never depends on the endpoint it tests.
/// </summary>
public static class ApplicationSeeds
{
    public static string Route(Guid scopeId, Guid applicationId) =>
        $"/api/scopes/{scopeId}/applications/{applicationId}/credentials";

    public static async Task<Scope> SeedScopeAsync(PostgresFixture db)
    {
        await using var context = db.CreateContext();
        var scope = new Scope { PublicId = Guid.NewGuid(), Name = $"scope-{Guid.NewGuid():N}" };
        context.Scopes.Add(scope);
        await context.SaveChangesAsync();
        return scope;
    }

    public static async Task<Person> SeedScopeAdminAsync(PostgresFixture db, Scope? ownedScope = null)
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

    public static async Task<Application> SeedApplicationAsync(PostgresFixture db, Scope scope, Person owner)
    {
        await using var context = db.CreateContext();
        var application = new Application
        {
            PublicId = Guid.NewGuid(), Name = $"app-{Guid.NewGuid():N}",
            ScopeId = scope.Id, OwnerId = owner.Id
        };
        context.Applications.Add(application);
        await context.SaveChangesAsync();
        return application;
    }

    /// <summary>A scope, its owning Scope Admin, and an application they own.</summary>
    public static async Task<(Scope Scope, Person Owner, Application Application)> SeedOwnedApplicationAsync(PostgresFixture db)
    {
        var scope = await SeedScopeAsync(db);
        var owner = await SeedScopeAdminAsync(db, scope);
        var application = await SeedApplicationAsync(db, scope, owner);
        return (scope, owner, application);
    }

    /// <summary>Stores a credential for <paramref name="application" /> and returns it with its secret.</summary>
    public static async Task<(ApplicationCredential Credential, string Secret)> SeedCredentialAsync(
        PostgresFixture db, Application application, DateTime? expiresAt = null, DateTime? revokedAt = null)
    {
        var secret = ApplicationCredentialSecret.New();
        await using var context = db.CreateContext();
        var credential = new ApplicationCredential
        {
            ApplicationId = application.Id,
            SecretHash = ApplicationCredentialSecret.HashOf(secret),
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = expiresAt,
            RevokedAt = revokedAt
        };
        context.ApplicationCredentials.Add(credential);
        await context.SaveChangesAsync();
        return (credential, secret);
    }

    public static async Task<List<ApplicationCredential>> CredentialsOfAsync(PostgresFixture db, Application application)
    {
        await using var context = db.CreateContext();
        return await context.ApplicationCredentials.AsNoTracking()
            .Where(x => x.ApplicationId == application.Id)
            .ToListAsync();
    }
}
