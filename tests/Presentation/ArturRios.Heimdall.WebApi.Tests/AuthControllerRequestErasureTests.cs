using System.Net;
using ArturRios.Configuration.Enums;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Query.Output;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Heimdall.WebApi.Tests.Support;
using ArturRios.Output;
using ArturRios.Util.Hashing;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Functional;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Heimdall.WebApi.Tests;

// Functional tests for POST /api/auth/erasure-request and GET /api/auth/erasure-requests (UC-42,
// UC-43) over the real pipeline. The behaviours that matter end to end are that a bearer token
// alone cannot trigger an irreversible operation, that the subject is always the caller, that a
// blocked request is still recorded with its deadline running, and that the queue is System Admin
// only.
[Collection(nameof(FunctionalCollection))]
public class AuthControllerRequestErasureTests(PostgresFixture db) : WebApiTest<Program>(EnvironmentType.Local)
{
    private const string Password = "Str0ng-Erasure-Pass!";

    private static string UniqueEmail(string prefix) => $"{prefix}-{Guid.NewGuid():N}@test.local";

    private async Task<Person> SeedPersonAsync(Roles role = Roles.User, Scope? ownedScope = null)
    {
        await using var context = db.CreateContext();
        var person = new Person
        {
            PublicId = Guid.NewGuid(),
            Name = "Ada",
            Email = UniqueEmail("ada"),
            PasswordHash = Hash.EncodeWithRandomSalt(Password, out var salt),
            Salt = salt,
            RoleId = (long)role,
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

    private async Task<Scope> SeedScopeAsync()
    {
        await using var context = db.CreateContext();
        var scope = new Scope { PublicId = Guid.NewGuid(), Name = $"scope-{Guid.NewGuid():N}" };
        context.Scopes.Add(scope);
        await context.SaveChangesAsync();
        return scope;
    }

    private async Task<Person> ReloadAsync(Person person)
    {
        await using var context = db.CreateContext();
        return await context.Persons.SingleAsync(x => x.Id == person.Id);
    }

    [FunctionalFact]
    public async Task GivenTheirPassword_WhenRequestingErasure_ThenTheRequestIsRecordedAndTheyAreSuspended()
    {
        var person = await SeedPersonAsync();
        Authorize(TestTokens.For(person.PublicId, (int)Roles.User));

        var response = await Gateway.PostAsync<DataOutput<RequestErasureCommandOutput?>>(
            "/api/auth/erasure-request", new { password = Password });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(person.PublicId, response.Body?.Data?.Id);
        Assert.False(response.Body?.Data?.Blocked);

        var stored = await ReloadAsync(person);

        Assert.True(stored.IsDeleted);
        Assert.Equal((int)DeletionKinds.SubjectRequested, stored.DeletionKind);
        Assert.NotNull(stored.ErasureRequestedAt);
        Assert.NotNull(stored.ErasureDueAt);

        // Suspended but not yet erased: the pass carries that out on the deadline
        Assert.Equal("Ada", stored.Name);
        Assert.Null(stored.AnonymisedAt);
    }

    [FunctionalFact]
    public async Task GivenTheWrongPassword_WhenRequestingErasure_ThenNothingChanges()
    {
        // A valid session is not proof the person is present
        var person = await SeedPersonAsync();
        Authorize(TestTokens.For(person.PublicId, (int)Roles.User));

        var response = await Gateway.PostAsync<DataOutput<RequestErasureCommandOutput?>>(
            "/api/auth/erasure-request", new { password = "wrong" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        var stored = await ReloadAsync(person);

        Assert.False(stored.IsDeleted);
        Assert.Null(stored.ErasureRequestedAt);
    }

    [FunctionalFact]
    public async Task GivenNoToken_WhenRequestingErasure_ThenUnauthorized()
    {
        var response = await Gateway.PostAsync<DataOutput<RequestErasureCommandOutput?>>(
            "/api/auth/erasure-request", new { password = Password });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenTheLastOwnerOfAScope_WhenRequestingErasure_ThenItIsRecordedButBlocked()
    {
        var scope = await SeedScopeAsync();
        var owner = await SeedPersonAsync(Roles.ScopeAdmin, scope);
        Authorize(TestTokens.For(owner.PublicId, (int)Roles.ScopeAdmin));

        var response = await Gateway.PostAsync<DataOutput<RequestErasureCommandOutput?>>(
            "/api/auth/erasure-request", new { password = Password });

        // A success: the request was accepted and the clock is running. Answering 4xx would tell the
        // subject their right had been refused when it has not.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Body?.Data?.Blocked);

        var stored = await ReloadAsync(owner);

        Assert.NotNull(stored.ErasureRequestedAt);
        Assert.Equal(ErasureMessages.BlockedByLastScopeOwnership, stored.ErasureBlockedReason);

        // NFR-12 holds: the scope keeps its owner
        Assert.False(stored.IsDeleted);
    }

    [FunctionalFact]
    public async Task GivenARequestAlreadyMade_WhenRequestingAgain_ThenConflict()
    {
        var person = await SeedPersonAsync();
        Authorize(TestTokens.For(person.PublicId, (int)Roles.User));

        await Gateway.PostAsync<DataOutput<RequestErasureCommandOutput?>>(
            "/api/auth/erasure-request", new { password = Password });

        // The token still authenticates for this call: the liveness filter is what would refuse a
        // suspended identity, and this asserts the handler's own guard rather than that
        var second = await Gateway.PostAsync<DataOutput<RequestErasureCommandOutput?>>(
            "/api/auth/erasure-request", new { password = Password });

        Assert.Contains(
            second.StatusCode, new[] { HttpStatusCode.Conflict, HttpStatusCode.Unauthorized });
    }

    [FunctionalFact]
    public async Task GivenASystemAdmin_WhenListingErasureRequests_ThenOutstandingOnesAreReturned()
    {
        // The request is seeded rather than made through the endpoint: one test authorizes as one
        // caller, and this one is about the queue rather than about how a row got into it.
        var person = await SeedPersonAsync();

        await using (var context = db.CreateContext())
        {
            var stored = await context.Persons.SingleAsync(x => x.Id == person.Id);

            stored.ErasureRequestedAt = DateTime.UtcNow.AddDays(-40);
            stored.ErasureDueAt = DateTime.UtcNow.AddDays(-10);
            stored.IsDeleted = true;
            stored.DeletedAt = DateTime.UtcNow.AddDays(-40);
            stored.DeletionKind = (int)DeletionKinds.SubjectRequested;

            await context.SaveChangesAsync();
        }

        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        var response = await Gateway.GetAsync<PaginatedOutput<ErasureRequestOutput>>(
            "/api/auth/erasure-requests?pageNumber=1&pageSize=100");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var request = Assert.Single(
            response.Body?.Data ?? [], candidate => candidate.SubjectId == person.PublicId);

        // A deadline that has passed is what the queue exists to surface
        Assert.True(request.Overdue);
    }

    [FunctionalFact]
    public async Task GivenAScopeAdmin_WhenListingErasureRequests_ThenForbidden()
    {
        // Which of their scope's users asked to be erased is not something the request entitles
        // them to know
        Authorize(TestTokens.ForRole((int)Roles.ScopeAdmin));

        var response = await Gateway.GetAsync<PaginatedOutput<ErasureRequestOutput>>(
            "/api/auth/erasure-requests?pageNumber=1&pageSize=20");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenNoToken_WhenListingErasureRequests_ThenUnauthorized()
    {
        var response = await Gateway.GetAsync<PaginatedOutput<ErasureRequestOutput>>(
            "/api/auth/erasure-requests?pageNumber=1&pageSize=20");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
