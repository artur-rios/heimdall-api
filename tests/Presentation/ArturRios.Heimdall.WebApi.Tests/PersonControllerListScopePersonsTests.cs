using System.Net;
using ArturRios.Configuration.Enums;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Query.Output;
using ArturRios.Heimdall.WebApi.Tests.Support;
using ArturRios.Output;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Functional;

namespace ArturRios.Heimdall.WebApi.Tests;

// Functional tests for GET /api/scopes/{scopeId}/persons (UC-07, FR-PE-04): the main flow for a
// System Admin and an owning Scope Admin, AF-07a (unknown scope → 404), AF-07b (non-owner → 403),
// and the framework-level authorization flows (403 for a plain User, 401 unauthenticated).
[Collection(nameof(FunctionalCollection))]
public class PersonControllerListScopePersonsTests(PostgresFixture db) : WebApiTest<Program>(EnvironmentType.Local)
{
    [FunctionalFact]
    public async Task GivenSystemAdmin_WhenGetScopePersons_ThenReturnsOnlyThatScopesUsers()
    {
        // Given two scopes, each with a User, and an owner on the first
        var scope = await Seeds.ScopeAsync(db);
        var otherScope = await Seeds.ScopeAsync(db);
        var member = await Seeds.PersonAsync(db, Roles.User, new PersonOptions { Name = "Ana", EmailPrefix = "user", EmailVerified = false, MemberOf = scope });
        await Seeds.PersonAsync(db, Roles.User, new PersonOptions { Name = "Carla", EmailPrefix = "user", EmailVerified = false, MemberOf = otherScope });
        var owner = await Seeds.PersonAsync(db, Roles.ScopeAdmin, new PersonOptions { EmailPrefix = "admin", Owns = [scope] });
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.GetAsync<PaginatedOutput<PersonOutput>>(
            $"/api/scopes/{scope.PublicId}/persons?pageNumber=1&pageSize=10");

        // Then — only the scope's User, not its owner and not the other scope's User
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, response.Body?.TotalItems);
        var returned = Assert.Single(response.Body!.Data!);
        Assert.Equal(member.PublicId, returned.Id);
        Assert.NotEqual(owner.PublicId, returned.Id);
        Assert.Equal(scope.PublicId, returned.ScopeId);
    }

    [FunctionalFact]
    public async Task GivenUsersSharingAName_WhenPagingThroughTheScope_ThenEveryUserAppearsExactlyOnce()
    {
        // Given six Users of whom four share a name — an ordinary situation, and one that used to
        // break paging. The list is ordered by name, which is not unique, and PostgreSQL gives no
        // ordering guarantee between rows whose sort key ties: each page is its own query, free to
        // break the tie differently. A caller walking the pages could see one person twice and miss
        // another entirely.
        var scope = await Seeds.ScopeAsync(db);
        var expected = new List<Guid>();

        foreach (var name in new[] { "Ana Silva", "Ana Silva", "Ana Silva", "Ana Silva", "Bruno", "Carla" })
        {
            expected.Add((await Seeds.PersonAsync(db, Roles.User, new PersonOptions { Name = name, EmailPrefix = "user", EmailVerified = false, MemberOf = scope })).PublicId);
        }

        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When every page is walked at a size that puts the boundary inside the tied run
        var seen = new List<Guid>();

        for (var pageNumber = 1; pageNumber <= 3; pageNumber++)
        {
            var page = await Gateway.GetAsync<PaginatedOutput<PersonOutput>>(
                $"/api/scopes/{scope.PublicId}/persons?pageNumber={pageNumber}&pageSize=2");

            Assert.Equal(HttpStatusCode.OK, page.StatusCode);
            Assert.Equal(6, page.Body!.TotalItems);

            seen.AddRange(page.Body.Data!.Select(person => person.Id));
        }

        // Then — six distinct people, and exactly the six that were seeded
        Assert.Equal(6, seen.Count);
        Assert.Equal(6, seen.Distinct().Count());
        Assert.Equal(expected.OrderBy(id => id), seen.OrderBy(id => id));
    }

    [FunctionalFact]
    public async Task GivenOwnerScopeAdmin_WhenGetScopePersons_ThenReturnsUsers()
    {
        // Given a ScopeAdmin who owns the scope
        var scope = await Seeds.ScopeAsync(db);
        var owner = await Seeds.PersonAsync(db, Roles.ScopeAdmin, new PersonOptions { EmailPrefix = "admin", Owns = [scope] });
        await Seeds.PersonAsync(db, Roles.User, new PersonOptions { Name = "Ana", EmailPrefix = "user", EmailVerified = false, MemberOf = scope });
        Authorize(TestTokens.For(owner.PublicId, (int)Roles.ScopeAdmin));

        // When
        var response = await Gateway.GetAsync<PaginatedOutput<PersonOutput>>(
            $"/api/scopes/{scope.PublicId}/persons?pageNumber=1&pageSize=10");

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, response.Body?.TotalItems);
    }

    [FunctionalFact]
    public async Task GivenNameFilter_WhenGetScopePersons_ThenReturnsMatchingUserOnly()
    {
        // Given two Users in the scope
        var scope = await Seeds.ScopeAsync(db);
        var ana = await Seeds.PersonAsync(db, Roles.User, new PersonOptions { Name = "Ana", EmailPrefix = "user", EmailVerified = false, MemberOf = scope });
        await Seeds.PersonAsync(db, Roles.User, new PersonOptions { Name = "Bruno", EmailPrefix = "user", EmailVerified = false, MemberOf = scope });
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.GetAsync<PaginatedOutput<PersonOutput>>(
            $"/api/scopes/{scope.PublicId}/persons?name=ana&pageNumber=1&pageSize=10");

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(ana.PublicId, Assert.Single(response.Body!.Data!).Id);
    }

    [FunctionalFact]
    public async Task GivenForgedActorInQueryString_WhenGetScopePersons_ThenTokenActorWins()
    {
        // Given a non-owning ScopeAdmin trying to impersonate a System Admin through the query string
        var scope = await Seeds.ScopeAsync(db);
        var admin = await Seeds.PersonAsync(db, Roles.ScopeAdmin, new PersonOptions { EmailPrefix = "admin" });
        Authorize(TestTokens.For(admin.PublicId, (int)Roles.ScopeAdmin));

        // When
        var response = await Gateway.GetAsync<PaginatedOutput<PersonOutput>>(
            $"/api/scopes/{scope.PublicId}/persons?actingRole=1&actingPersonId=1&pageNumber=1&pageSize=10");

        // Then — the forged values are discarded and the real actor is rejected (AF-07b)
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenScopeAdminNotOwningScope_WhenGetScopePersons_ThenForbidden()
    {
        // Given a ScopeAdmin who does not own the scope (AF-07b)
        var scope = await Seeds.ScopeAsync(db);
        var admin = await Seeds.PersonAsync(db, Roles.ScopeAdmin, new PersonOptions { EmailPrefix = "admin" });
        Authorize(TestTokens.For(admin.PublicId, (int)Roles.ScopeAdmin));

        // When
        var response = await Gateway.GetAsync<PaginatedOutput<PersonOutput>>(
            $"/api/scopes/{scope.PublicId}/persons?pageNumber=1&pageSize=10");

        // Then
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenUnknownScope_WhenGetScopePersons_ThenNotFound()
    {
        // Given (AF-07a)
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.GetAsync<PaginatedOutput<PersonOutput>>(
            $"/api/scopes/{Guid.NewGuid()}/persons?pageNumber=1&pageSize=10");

        // Then
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenPlainUserCaller_WhenGetScopePersons_ThenForbidden()
    {
        // Given a User, whom the role gate rejects before the handler runs
        var scope = await Seeds.ScopeAsync(db);
        Authorize(TestTokens.ForRole((int)Roles.User));

        // When
        var response = await Gateway.GetAsync<PaginatedOutput<PersonOutput>>(
            $"/api/scopes/{scope.PublicId}/persons?pageNumber=1&pageSize=10");

        // Then
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenNoToken_WhenGetScopePersons_ThenUnauthorized()
    {
        // Given a scope but no bearer token on the gateway
        var scope = await Seeds.ScopeAsync(db);

        // When
        var response = await Gateway.GetAsync<PaginatedOutput<PersonOutput>>(
            $"/api/scopes/{scope.PublicId}/persons?pageNumber=1&pageSize=10");

        // Then
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
