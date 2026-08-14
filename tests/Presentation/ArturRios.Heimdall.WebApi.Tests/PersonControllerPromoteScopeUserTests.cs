using System.Net;
using ArturRios.Configuration.Enums;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Heimdall.WebApi.Tests.Support;
using ArturRios.Output;
using ArturRios.Util.Http;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Functional;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Heimdall.WebApi.Tests;

// Functional tests for POST /api/scopes/{scopeId}/users/{personId}/promote (UC-23,
// FR-SC-08/FR-SC-13/FR-RO-03): the main flow for a System Admin and for an existing owner, AF-23a
// (scope unknown or logically deleted), AF-23b (person unknown, logically deleted, or a User of
// another scope), AF-23c (a Scope Admin who owns a different scope), AF-23d (already a ScopeAdmin,
// which is also what a repeated call meets), the FR-PE-09 admin-namespace guard, and the framework
// flows the actor list produces (403 for a User, 401 unauthenticated).
[Collection(nameof(FunctionalCollection))]
public class PersonControllerPromoteScopeUserTests(PostgresFixture db) : WebApiTest<Program>(EnvironmentType.Local)
{
    private static string Route(Guid scopeId, Guid personId) =>
        $"/api/scopes/{scopeId}/users/{personId}/promote";

    /// <summary>
    ///     The request carries no body — both identifiers are route values — but
    ///     <c>HttpGateway.PostAsync</c> takes a payload, so an empty object is sent and the action,
    ///     which binds no body, ignores it.
    /// </summary>
    private Task<HttpOutput<DataOutput<PromoteScopeUserCommandOutput?>?>> PromoteAsync(Guid scopeId, Guid personId) =>
        Gateway.PostAsync<DataOutput<PromoteScopeUserCommandOutput?>>(Route(scopeId, personId), new { });

    private async Task<Scope> SeedScopeAsync(bool isDeleted = false)
    {
        await using var context = db.CreateContext();
        var scope = new Scope { PublicId = Guid.NewGuid(), Name = $"scope-{Guid.NewGuid():N}", IsDeleted = isDeleted };
        context.Scopes.Add(scope);
        await context.SaveChangesAsync();
        return scope;
    }

    private async Task<Person> SeedScopeAdminAsync(Scope? ownedScope = null, string? email = null)
    {
        await using var context = db.CreateContext();
        var person = new Person
        {
            PublicId = Guid.NewGuid(), Name = "Admin",
            Email = email ?? $"admin-{Guid.NewGuid():N}@test.local",
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

    private async Task<Person> SeedUserAsync(Scope scope, bool isDeleted = false, string? email = null)
    {
        await using var context = db.CreateContext();
        var person = new Person
        {
            PublicId = Guid.NewGuid(), Name = "Member",
            Email = email ?? $"user-{Guid.NewGuid():N}@test.local",
            RoleId = (long)Roles.User, EmailVerified = true, IsDeleted = isDeleted
        };
        context.Persons.Add(person);
        await context.SaveChangesAsync();

        context.ScopeUsers.Add(new ScopeUser { ScopeId = scope.Id, PersonId = person.Id });
        // Person.ScopeId mirrors the membership row, as the application writes it — without
        // it the seeded User sits outside the per-scope uniqueness index.
        person.ScopeId = scope.Id;
        await context.SaveChangesAsync();

        return person;
    }

    private async Task<long> RoleOfAsync(Person person)
    {
        await using var context = db.CreateContext();
        return (await context.Persons.AsNoTracking().SingleAsync(p => p.Id == person.Id)).RoleId;
    }

    private async Task<int> MembershipCountAsync(Person person)
    {
        await using var context = db.CreateContext();
        return await context.ScopeUsers.CountAsync(su => su.PersonId == person.Id);
    }

    private async Task<int> OwnershipCountAsync(Scope scope, Person person)
    {
        await using var context = db.CreateContext();
        return await context.ScopeOwners.CountAsync(so => so.ScopeId == scope.Id && so.PersonId == person.Id);
    }

    /// <summary>Asserts the person was left exactly as seeded: still a User, still a member, owning nothing.</summary>
    private async Task AssertNotPromotedAsync(Scope scope, Person person)
    {
        Assert.Equal((long)Roles.User, await RoleOfAsync(person));
        Assert.Equal(1, await MembershipCountAsync(person));
        Assert.Equal(0, await OwnershipCountAsync(scope, person));
    }

    [FunctionalFact]
    public async Task GivenSystemAdmin_WhenPostPromote_ThenOkAndRowsAreMoved()
    {
        // Given a scope and a User belonging to it (UC-23 main flow)
        var scope = await SeedScopeAsync();
        var person = await SeedUserAsync(scope);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await PromoteAsync(scope.PublicId, person.PublicId);

        // Then — response
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(person.PublicId, response.Body?.Data?.Id);
        Assert.Equal((int)Roles.ScopeAdmin, response.Body?.Data?.Role);
        Assert.Equal(scope.PublicId, Assert.Single(response.Body!.Data!.OwnedScopeIds));
        Assert.Contains(PersonMessages.ScopeUserPromotedSuccessfully, response.Body!.Messages);

        // Then — database state: the SCOPE_USER row is gone and a SCOPE_OWNER row took its place
        Assert.Equal((long)Roles.ScopeAdmin, await RoleOfAsync(person));
        Assert.Equal(0, await MembershipCountAsync(person));
        Assert.Equal(1, await OwnershipCountAsync(scope, person));
    }

    [FunctionalFact]
    public async Task GivenExistingOwner_WhenPostPromote_ThenOkAndRowsAreMoved()
    {
        // Given a Scope Admin who already owns the scope promoting one of its Users (FR-SC-13)
        var scope = await SeedScopeAsync();
        var actor = await SeedScopeAdminAsync(ownedScope: scope);
        var person = await SeedUserAsync(scope);
        Authorize(TestTokens.For(actor.PublicId, (int)Roles.ScopeAdmin));

        // When
        var response = await PromoteAsync(scope.PublicId, person.PublicId);

        // Then — the scope now has both owners and the promoted person is no longer a member
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, await MembershipCountAsync(person));
        Assert.Equal(1, await OwnershipCountAsync(scope, person));
        Assert.Equal(1, await OwnershipCountAsync(scope, actor));
    }

    [FunctionalFact]
    public async Task GivenScopeAdminOfAnotherScope_WhenPostPromote_ThenForbidden()
    {
        // Given a Scope Admin who owns some other scope (AF-23c)
        var scope = await SeedScopeAsync();
        var otherScope = await SeedScopeAsync();
        var actor = await SeedScopeAdminAsync(ownedScope: otherScope);
        var person = await SeedUserAsync(scope);
        Authorize(TestTokens.For(actor.PublicId, (int)Roles.ScopeAdmin));

        // When
        var response = await PromoteAsync(scope.PublicId, person.PublicId);

        // Then
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await AssertNotPromotedAsync(scope, person);
    }

    [FunctionalFact]
    public async Task GivenUserRole_WhenPostPromote_ThenForbidden()
    {
        // Given a caller holding the User role — refused by the attribute, before any handler runs
        var scope = await SeedScopeAsync();
        var person = await SeedUserAsync(scope);
        var caller = await SeedUserAsync(scope);
        Authorize(TestTokens.For(caller.PublicId, (int)Roles.User));

        // When
        var response = await PromoteAsync(scope.PublicId, person.PublicId);

        // Then
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await AssertNotPromotedAsync(scope, person);
    }

    [FunctionalFact]
    public async Task GivenUnknownScope_WhenPostPromote_ThenNotFound()
    {
        // AF-23a
        var scope = await SeedScopeAsync();
        var person = await SeedUserAsync(scope);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        var response = await PromoteAsync(Guid.NewGuid(), person.PublicId);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains(PersonMessages.ScopeNotFound, response.Body!.Errors);
        await AssertNotPromotedAsync(scope, person);
    }

    [FunctionalFact]
    public async Task GivenLogicallyDeletedScope_WhenPostPromote_ThenNotFound()
    {
        // AF-23a treats a logically deleted scope as absent
        var scope = await SeedScopeAsync(isDeleted: true);
        var person = await SeedUserAsync(scope);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        var response = await PromoteAsync(scope.PublicId, person.PublicId);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains(PersonMessages.ScopeNotFound, response.Body!.Errors);
        await AssertNotPromotedAsync(scope, person);
    }

    [FunctionalFact]
    public async Task GivenUnknownPerson_WhenPostPromote_ThenBadRequest()
    {
        // AF-23b
        var scope = await SeedScopeAsync();
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        var response = await PromoteAsync(scope.PublicId, Guid.NewGuid());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(PersonMessages.PersonNotScopeUser, response.Body!.Errors);
    }

    [FunctionalFact]
    public async Task GivenLogicallyDeletedUser_WhenPostPromote_ThenBadRequest()
    {
        // AF-23b — a deleted person can no longer authenticate, so the ownership would be unusable
        var scope = await SeedScopeAsync();
        var person = await SeedUserAsync(scope, isDeleted: true);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        var response = await PromoteAsync(scope.PublicId, person.PublicId);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(PersonMessages.PersonNotScopeUser, response.Body!.Errors);
        await AssertNotPromotedAsync(scope, person);
    }

    [FunctionalFact]
    public async Task GivenUserOfAnotherScope_WhenPostPromote_ThenBadRequest()
    {
        // AF-23b — the person is a User, but not of the scope named in the route
        var scope = await SeedScopeAsync();
        var otherScope = await SeedScopeAsync();
        var person = await SeedUserAsync(otherScope);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        var response = await PromoteAsync(scope.PublicId, person.PublicId);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(PersonMessages.PersonNotScopeUser, response.Body!.Errors);
        await AssertNotPromotedAsync(otherScope, person);
        Assert.Equal(0, await OwnershipCountAsync(scope, person));
    }

    [FunctionalFact]
    public async Task GivenScopeAdminPerson_WhenPostPromote_ThenConflict()
    {
        // AF-23d — there is no role left to promote them to
        var scope = await SeedScopeAsync();
        var person = await SeedScopeAdminAsync(ownedScope: scope);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        var response = await PromoteAsync(scope.PublicId, person.PublicId);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains(PersonMessages.AlreadyScopeAdmin, response.Body!.Errors);
        Assert.Equal(1, await OwnershipCountAsync(scope, person));
    }

    [FunctionalFact]
    public async Task GivenPersonPromotedTwice_WhenPostPromote_ThenSecondCallIsConflict()
    {
        // AF-23d is what a repeated call meets: the first promotion made them a ScopeAdmin. The
        // promotion is not idempotent the way UC-21 is — there the second call finds nothing to do,
        // here it finds the role already held — and the ownership row is never written twice.
        var scope = await SeedScopeAsync();
        var person = await SeedUserAsync(scope);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var first = await PromoteAsync(scope.PublicId, person.PublicId);
        var second = await PromoteAsync(scope.PublicId, person.PublicId);

        // Then
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Contains(PersonMessages.AlreadyScopeAdmin, second.Body!.Errors);
        Assert.Equal(1, await OwnershipCountAsync(scope, person));
        Assert.Equal(0, await MembershipCountAsync(person));
    }

    [FunctionalFact]
    public async Task GivenEmailAlreadyUsedByAnAdmin_WhenPostPromote_ThenConflict()
    {
        // FR-PE-09 — the promotion would move the address into the admin namespace, where it is
        // already taken. UC-23 defines no flow of its own for this, so the mapped 409 is reused.
        var scope = await SeedScopeAsync();
        var email = $"shared-{Guid.NewGuid():N}@test.local";
        await SeedScopeAdminAsync(email: email);
        var person = await SeedUserAsync(scope, email: email);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        var response = await PromoteAsync(scope.PublicId, person.PublicId);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains(PersonMessages.EmailAlreadyExists, response.Body!.Errors);
        await AssertNotPromotedAsync(scope, person);
    }

    [FunctionalFact]
    public async Task GivenNoToken_WhenPostPromote_ThenUnauthorized()
    {
        var scope = await SeedScopeAsync();
        var person = await SeedUserAsync(scope);

        var response = await PromoteAsync(scope.PublicId, person.PublicId);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await AssertNotPromotedAsync(scope, person);
    }
}
