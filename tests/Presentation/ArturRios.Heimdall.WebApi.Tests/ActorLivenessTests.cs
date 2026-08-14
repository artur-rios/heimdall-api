using System.Net;
using ArturRios.Configuration.Enums;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Query.Output;
using ArturRios.Heimdall.WebApi.Security;
using ArturRios.Heimdall.WebApi.Tests.Support;
using ArturRios.Output;
using ArturRios.Util.Hashing;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Functional;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Heimdall.WebApi.Tests;

// Functional tests for ActorLivenessFilter (FR-AU-05, FR-GO-12): a bearer token naming an identity
// that has been logically deleted, or that never existed, is refused everywhere.
//
// Authentication runs in ClaimsOnly mode, so a token outlives the identity it names for its whole
// lifetime — an hour by default. Some handlers used to compensate individually and some did not:
// ScopeOwnershipChecker excluded a deleted Scope Admin, while every System Admin bypass and every
// "acting on yourself" branch trusted the role claim alone, so the protection covered the lesser
// role and not the greater one. These tests pin the rule at the pipeline, where it is uniform.
[Collection(nameof(FunctionalCollection))]
public class ActorLivenessTests(PostgresFixture db) : WebApiTest<Program>(EnvironmentType.Local)
{
    private const string Password = "Str0ng-Liveness-Pass!";

    private static string UniqueEmail(string prefix) => $"{prefix}-{Guid.NewGuid():N}@test.local";

    private async Task<Person> SeedPersonAsync(Roles role, bool isDeleted = false)
    {
        await using var context = db.CreateContext();
        var person = new Person
        {
            PublicId = Guid.NewGuid(),
            Name = $"{role}",
            Email = UniqueEmail($"liveness-{role}"),
            PasswordHash = Hash.EncodeWithRandomSalt(Password, out var salt),
            Salt = salt,
            RoleId = (long)role,
            EmailVerified = true,
            IsDeleted = isDeleted
        };
        context.Persons.Add(person);
        await context.SaveChangesAsync();
        return person;
    }

    private async Task<Scope> SeedScopeAsync()
    {
        await using var context = db.CreateContext();
        var scope = new Scope { PublicId = Guid.NewGuid(), Name = $"scope-{Guid.NewGuid():N}" };
        context.Scopes.Add(scope);
        await context.SaveChangesAsync();
        return scope;
    }

    private async Task DeleteAsync(Person person)
    {
        await using var context = db.CreateContext();
        var stored = await context.Persons.FirstAsync(x => x.PublicId == person.PublicId);
        stored.IsDeleted = true;
        await context.SaveChangesAsync();
    }

    [FunctionalFact]
    public async Task GivenLogicallyDeletedSystemAdmin_WhenCallingAnyEndpoint_ThenUnauthorized()
    {
        // Given a System Admin who held a valid token when another administrator deleted them. This
        // is the case that mattered most and was covered least: a System Admin bypasses every
        // ownership check, so for the rest of the token's life they could still delete or hard-delete
        // any person, change any role, and create or destroy scopes.
        var admin = await SeedPersonAsync(Roles.SystemAdmin);
        var target = await SeedPersonAsync(Roles.User);
        Authorize(TestTokens.For(admin.PublicId, (int)Roles.SystemAdmin));

        // The token works while they are live
        var before = await Gateway.GetAsync<DataOutput<PersonOutput?>>($"/api/persons/{target.PublicId}");
        Assert.Equal(HttpStatusCode.OK, before.StatusCode);

        // When they are logically deleted
        await DeleteAsync(admin);

        // Then the same token is refused — on a read
        var read = await Gateway.GetAsync<DataOutput<PersonOutput?>>($"/api/persons/{target.PublicId}");
        Assert.Equal(HttpStatusCode.Unauthorized, read.StatusCode);
        Assert.Contains(ActorLivenessFilter.ActorNotLive, read.Body!.Errors);

        // Then — and on a write, which is the half that could not be undone
        var delete = await Gateway.DeleteAsync<DataOutput<object?>>($"/api/persons/{target.PublicId}");
        Assert.Equal(HttpStatusCode.Unauthorized, delete.StatusCode);

        // Then — the target is untouched
        await using var context = db.CreateContext();
        Assert.False((await context.Persons.FirstAsync(x => x.PublicId == target.PublicId)).IsDeleted);
    }

    [FunctionalFact]
    public async Task GivenLogicallyDeletedPerson_WhenActingOnThemselves_ThenUnauthorized()
    {
        // Given the "acting on yourself" branch, which granted access on the claim alone — a deleted
        // person could still read and rewrite their own record
        var person = await SeedPersonAsync(Roles.User, isDeleted: true);
        Authorize(TestTokens.For(person.PublicId, (int)Roles.User));

        // When
        var response = await Gateway.GetAsync<DataOutput<PersonOutput?>>($"/api/persons/{person.PublicId}");

        // Then
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(ActorLivenessFilter.ActorNotLive, response.Body!.Errors);
    }

    [FunctionalFact]
    public async Task GivenTokenNamingNobody_WhenCallingAnEndpoint_ThenUnauthorized()
    {
        // Given what a hard deletion (UC-10, UC-29) leaves behind: a correctly signed token whose
        // subject is in neither identity table
        Authorize(TestTokens.For(Guid.NewGuid(), (int)Roles.SystemAdmin));

        // When
        var response = await Gateway.GetAsync<PaginatedOutput<ScopeOutput>>("/api/scopes?pageNumber=1&pageSize=10");

        // Then
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenLogicallyDeletedGoogleUser_WhenCallingAnEndpoint_ThenUnauthorized()
    {
        // Given a Google-authenticated caller UC-28 deleted. A Google token names a GOOGLE_USER row,
        // not a person, so the filter has to consult both tables or it would refuse every Google
        // User outright.
        var scope = await SeedScopeAsync();

        await using (var context = db.CreateContext())
        {
            context.GoogleUsers.Add(new GoogleUser
            {
                PublicId = Guid.NewGuid(),
                GoogleId = $"google-{Guid.NewGuid():N}",
                Name = "Deleted Google User",
                Email = UniqueEmail("google-deleted"),
                ScopeId = scope.Id,
                IsDeleted = true
            });
            await context.SaveChangesAsync();
        }

        Guid googleUserId;

        await using (var context = db.CreateContext())
        {
            googleUserId = (await context.GoogleUsers.OrderBy(x => x.Id).LastAsync()).PublicId;
        }

        Authorize(TestTokens.For(googleUserId, (int)Roles.User, scope.PublicId));

        // When
        var response = await Gateway.PostAsync<DataOutput<object?>>("/api/auth/google/sign-out", new { });

        // Then
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(ActorLivenessFilter.ActorNotLive, response.Body!.Errors);
    }

    [FunctionalFact]
    public async Task GivenLiveGoogleUser_WhenCallingAnEndpoint_ThenTheFilterAllowsIt()
    {
        // Given a live Google User: the filter must not refuse an identity that only exists in the
        // GOOGLE_USER table, which is the failure mode of checking PERSON alone.
        var scope = await SeedScopeAsync();

        await using (var context = db.CreateContext())
        {
            context.GoogleUsers.Add(new GoogleUser
            {
                PublicId = Guid.NewGuid(),
                GoogleId = $"google-{Guid.NewGuid():N}",
                Name = "Live Google User",
                Email = UniqueEmail("google-live"),
                ScopeId = scope.Id
            });
            await context.SaveChangesAsync();
        }

        Guid googleUserId;

        await using (var context = db.CreateContext())
        {
            googleUserId = (await context.GoogleUsers.OrderBy(x => x.Id).LastAsync()).PublicId;
        }

        Authorize(TestTokens.For(googleUserId, (int)Roles.User, scope.PublicId));

        // When
        var response = await Gateway.PostAsync<DataOutput<object?>>("/api/auth/google/sign-out", new { });

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenNoBearerToken_WhenCallingAnAnonymousEndpoint_ThenTheFilterIsANoOp()
    {
        // Given an anonymous endpoint. The filter narrows an identity the pipeline already attached
        // and never requires one of its own, so [AllowAnonymous] is unaffected.
        var response = await Gateway.GetAsync<DataOutput<object?>>("/HealthCheck");

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
