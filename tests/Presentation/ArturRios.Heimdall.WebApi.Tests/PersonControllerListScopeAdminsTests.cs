using System.Net;
using ArturRios.Configuration.Enums;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Query.Output;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Heimdall.WebApi.Tests.Support;
using ArturRios.Output;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Functional;

namespace ArturRios.Heimdall.WebApi.Tests;

// Functional tests for GET /api/persons/scope-admins (UC-07 read d, FR-PE-12): the main flow for a
// System Admin and a Scope Admin, the excludeOwnersOfScopeId exclusion and its ownership gate
// (AF-07a unknown scope → 404, AF-07b non-owner → 403), and the framework-level authorization flows
// (403 for a plain User, 401 unauthenticated).
[Collection(nameof(FunctionalCollection))]
public class PersonControllerListScopeAdminsTests(PostgresFixture db) : WebApiTest<Program>(EnvironmentType.Local)
{
    [FunctionalFact]
    public async Task GivenSystemAdmin_WhenListScopeAdmins_ThenReturnsEveryLiveScopeAdmin()
    {
        // Given two Scope Admins with a shared, distinctive name fragment
        var marker = $"pick{Guid.NewGuid():N}";
        await Seeds.PersonAsync(db, Roles.ScopeAdmin, new PersonOptions { Name = $"Ana {marker}", EmailPrefix = "admin" });
        await Seeds.PersonAsync(db, Roles.ScopeAdmin, new PersonOptions { Name = $"Bruno {marker}", EmailPrefix = "admin" });
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.GetAsync<PaginatedOutput<PersonSummaryOutput>>(
            $"/api/persons/scope-admins?pageNumber=1&pageSize=10&name={marker}");

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, response.Body?.TotalItems);
    }

    [FunctionalFact]
    public async Task GivenSystemAdmin_WhenListScopeAdmins_ThenSystemAdminsAreOfferedAsOwnersToo()
    {
        // Given a Scope Admin and a System Admin — either may own a scope (FR-SC-08)
        var marker = $"pick{Guid.NewGuid():N}";
        await Seeds.PersonAsync(db, Roles.ScopeAdmin, new PersonOptions { Name = $"Ana {marker}", EmailPrefix = "admin" });
        await Seeds.PersonAsync(db, Roles.SystemAdmin, new PersonOptions { Name = $"Root {marker}", EmailPrefix = "admin" });
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.GetAsync<PaginatedOutput<PersonSummaryOutput>>(
            $"/api/persons/scope-admins?pageNumber=1&pageSize=10&name={marker}");

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, response.Body?.TotalItems);
    }

    [FunctionalFact]
    public async Task GivenScopeAdmin_WhenListScopeAdmins_ThenSystemAdminsAreNotListed()
    {
        // Given a Scope Admin looking for co-owners while a System Admin matches the filter too —
        // a System Admin's address is not tenant-facing (FR-PE-12)
        var marker = $"pick{Guid.NewGuid():N}";
        var scope = await Seeds.ScopeAsync(db);
        var caller = await Seeds.PersonAsync(db, Roles.ScopeAdmin, new PersonOptions { Name = $"Caller {marker}", EmailPrefix = "admin", Owns = [scope] });
        await Seeds.PersonAsync(db, Roles.SystemAdmin, new PersonOptions { Name = $"Root {marker}", EmailPrefix = "admin" });
        Authorize(TestTokens.For(caller.PublicId, (int)Roles.ScopeAdmin, null, scope.PublicId));

        // When
        var response = await Gateway.GetAsync<PaginatedOutput<PersonSummaryOutput>>(
            $"/api/persons/scope-admins?pageNumber=1&pageSize=10&name={marker}");

        // Then — only the caller
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(caller.PublicId, Assert.Single(response.Body!.Data!).Id);
    }

    [FunctionalFact]
    public async Task GivenScopeAdmin_WhenListScopeAdmins_ThenTheyMayReadTheListing()
    {
        // Given a Scope Admin who shares no scope with the administrator they are looking for —
        // the case UC-07's own visibility rule could never surface (UI-14 step 3)
        var marker = $"pick{Guid.NewGuid():N}";
        var scope = await Seeds.ScopeAsync(db);
        var caller = await Seeds.PersonAsync(db, Roles.ScopeAdmin, new PersonOptions { Name = $"Caller {marker}", EmailPrefix = "admin", Owns = [scope] });
        await Seeds.PersonAsync(db, Roles.ScopeAdmin, new PersonOptions { Name = $"Stranger {marker}", EmailPrefix = "admin" });
        Authorize(TestTokens.For(caller.PublicId, (int)Roles.ScopeAdmin, null, scope.PublicId));

        // When
        var response = await Gateway.GetAsync<PaginatedOutput<PersonSummaryOutput>>(
            $"/api/persons/scope-admins?pageNumber=1&pageSize=10&name={marker}");

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, response.Body?.TotalItems);
    }

    [FunctionalFact]
    public async Task GivenExcludeOwnersOfOwnScope_WhenListScopeAdmins_ThenCurrentOwnersAreRemoved()
    {
        // Given a scope the caller owns, and one other administrator (UI-14 AF-14c)
        var marker = $"pick{Guid.NewGuid():N}";
        var scope = await Seeds.ScopeAsync(db);
        var caller = await Seeds.PersonAsync(db, Roles.ScopeAdmin, new PersonOptions { Name = $"Owner {marker}", EmailPrefix = "admin", Owns = [scope] });
        var candidate = await Seeds.PersonAsync(db, Roles.ScopeAdmin, new PersonOptions { Name = $"Candidate {marker}", EmailPrefix = "admin" });
        Authorize(TestTokens.For(caller.PublicId, (int)Roles.ScopeAdmin, null, scope.PublicId));

        // When
        var response = await Gateway.GetAsync<PaginatedOutput<PersonSummaryOutput>>(
            $"/api/persons/scope-admins?pageNumber=1&pageSize=10&name={marker}" +
            $"&excludeOwnersOfScopeId={scope.PublicId}");

        // Then — the caller, already an owner, is gone; the candidate remains
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, response.Body?.TotalItems);
        Assert.Equal(candidate.PublicId, response.Body?.Data?.Single().Id);
    }

    [FunctionalFact]
    public async Task GivenExcludeOwnersOfAnotherScope_WhenListScopeAdmins_ThenReturnsForbidden()
    {
        // Given a Scope Admin naming a scope they do not own. This is the regression test for the
        // enumeration leak: with/without diffing would otherwise reveal that scope's owners.
        var scope = await Seeds.ScopeAsync(db);
        var otherScope = await Seeds.ScopeAsync(db);
        var caller = await Seeds.PersonAsync(db, Roles.ScopeAdmin, new PersonOptions { Name = "Admin", EmailPrefix = "admin", Owns = [scope] });
        Authorize(TestTokens.For(caller.PublicId, (int)Roles.ScopeAdmin, null, scope.PublicId));

        // When
        var response = await Gateway.GetAsync<PaginatedOutput<PersonSummaryOutput>>(
            $"/api/persons/scope-admins?pageNumber=1&pageSize=10&excludeOwnersOfScopeId={otherScope.PublicId}");

        // Then
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains(PersonMessages.NotScopeOwner, response.Body!.Errors);
    }

    [FunctionalFact]
    public async Task GivenUnknownScopeToExclude_WhenListScopeAdmins_ThenReturnsNotFound()
    {
        // Given no such scope
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.GetAsync<PaginatedOutput<PersonSummaryOutput>>(
            $"/api/persons/scope-admins?pageNumber=1&pageSize=10&excludeOwnersOfScopeId={Guid.NewGuid()}");

        // Then
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenPlainUser_WhenListScopeAdmins_ThenReturnsForbidden()
    {
        // Given a User, whom the role gate excludes
        Authorize(TestTokens.ForRole((int)Roles.User));

        // When
        var response = await Gateway.GetAsync<PaginatedOutput<PersonSummaryOutput>>(
            "/api/persons/scope-admins?pageNumber=1&pageSize=10");

        // Then
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenUnauthenticatedCaller_WhenListScopeAdmins_ThenReturnsUnauthorized()
    {
        // Given no bearer token
        // When
        var response = await Gateway.GetAsync<PaginatedOutput<PersonSummaryOutput>>(
            "/api/persons/scope-admins?pageNumber=1&pageSize=10");

        // Then
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
