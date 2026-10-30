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

// Functional tests for GET /api/persons/system-admins (UC-07 read e, FR-PE-13): the main flow for a
// System Admin (only live, unrestricted System Admins, filtered and paged), the NFR-10 validation
// refusals (400), and the framework-level authorization flows (403 for a Scope Admin and a User,
// 401 unauthenticated).
[Collection(nameof(FunctionalCollection))]
public class PersonControllerListSystemAdminsTests(PostgresFixture db) : WebApiTest<Program>(EnvironmentType.Local)
{
    [FunctionalFact]
    public async Task GivenSystemAdmin_WhenListSystemAdmins_ThenReturnsEveryLiveSystemAdmin()
    {
        // Given two System Admins sharing a distinctive name fragment, and — matching it too — a Scope
        // Admin, a deleted System Admin, and a restricted System Admin, none of whom may appear
        var marker = $"root{Guid.NewGuid():N}";
        var ana = await Seeds.PersonAsync(db, Roles.SystemAdmin, new PersonOptions { Name = $"Ana {marker}", EmailPrefix = "admin", Configure = p => p.DeletedAt = false ? DateTime.UtcNow : null });
        var bruno = await Seeds.PersonAsync(db, Roles.SystemAdmin, new PersonOptions { Name = $"Bruno {marker}", EmailPrefix = "admin", Configure = p => p.DeletedAt = false ? DateTime.UtcNow : null });
        await Seeds.PersonAsync(db, Roles.ScopeAdmin, new PersonOptions { Name = $"Carla {marker}", EmailPrefix = "admin", Configure = p => p.DeletedAt = false ? DateTime.UtcNow : null });
        await Seeds.PersonAsync(db, Roles.SystemAdmin, new PersonOptions { Name = $"Diego {marker}", EmailPrefix = "admin", IsDeleted = true, Configure = p => p.DeletedAt = true ? DateTime.UtcNow : null });
        await Seeds.PersonAsync(db, Roles.SystemAdmin, new PersonOptions { Name = $"Eva {marker}", EmailPrefix = "admin", RestrictedAt = DateTime.UtcNow, Configure = p => p.DeletedAt = false ? DateTime.UtcNow : null });
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.GetAsync<PaginatedOutput<PersonSummaryOutput>>(
            $"/api/persons/system-admins?pageNumber=1&pageSize=10&name={marker}");

        // Then — ordered by name
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, response.Body?.TotalItems);
        Assert.Equal([ana.PublicId, bruno.PublicId], response.Body!.Data!.Select(x => x.Id));
        Assert.Contains(PersonMessages.PersonsRetrievedSuccessfully, response.Body.Messages);
    }

    [FunctionalFact]
    public async Task GivenEmailFilter_WhenListSystemAdmins_ThenMatchesCaseInsensitiveSubstring()
    {
        // Given a System Admin whose address is searched for in upper case
        var admin = await Seeds.PersonAsync(db, Roles.SystemAdmin, new PersonOptions { Name = "Root", EmailPrefix = "admin", Configure = p => p.DeletedAt = false ? DateTime.UtcNow : null });
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.GetAsync<PaginatedOutput<PersonSummaryOutput>>(
            $"/api/persons/system-admins?pageNumber=1&pageSize=10&email={admin.Email.ToUpperInvariant()}");

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var listed = Assert.Single(response.Body!.Data!);
        Assert.Equal(admin.PublicId, listed.Id);
        Assert.Equal(admin.Email, listed.Email);
    }

    [FunctionalFact]
    public async Task GivenMoreSystemAdminsThanAPage_WhenListSystemAdmins_ThenReturnsTheRequestedPage()
    {
        // Given three matching System Admins and a page size of two
        var marker = $"root{Guid.NewGuid():N}";
        await Seeds.PersonAsync(db, Roles.SystemAdmin, new PersonOptions { Name = $"Ana {marker}", EmailPrefix = "admin", Configure = p => p.DeletedAt = false ? DateTime.UtcNow : null });
        await Seeds.PersonAsync(db, Roles.SystemAdmin, new PersonOptions { Name = $"Bruno {marker}", EmailPrefix = "admin", Configure = p => p.DeletedAt = false ? DateTime.UtcNow : null });
        var carla = await Seeds.PersonAsync(db, Roles.SystemAdmin, new PersonOptions { Name = $"Carla {marker}", EmailPrefix = "admin", Configure = p => p.DeletedAt = false ? DateTime.UtcNow : null });
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.GetAsync<PaginatedOutput<PersonSummaryOutput>>(
            $"/api/persons/system-admins?pageNumber=2&pageSize=2&name={marker}");

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, response.Body?.TotalItems);
        Assert.Equal(carla.PublicId, Assert.Single(response.Body!.Data!).Id);
    }

    [FunctionalFact]
    public async Task GivenPageSizeAboveTheBound_WhenListSystemAdmins_ThenReturnsBadRequest()
    {
        // Given NFR-10's page-size bound exceeded
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.GetAsync<PaginatedOutput<PersonSummaryOutput>>(
            "/api/persons/system-admins?pageNumber=1&pageSize=101");

        // Then
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(PaginationMessages.InvalidPageSize, response.Body!.Errors);
    }

    [FunctionalFact]
    public async Task GivenPageNumberBelowOne_WhenListSystemAdmins_ThenReturnsBadRequest()
    {
        // Given NFR-10's page-number bound violated
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.GetAsync<PaginatedOutput<PersonSummaryOutput>>(
            "/api/persons/system-admins?pageNumber=0&pageSize=10");

        // Then
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(PaginationMessages.InvalidPageNumber, response.Body!.Errors);
    }

    [FunctionalFact]
    public async Task GivenNameFilterLongerThanColumn_WhenListSystemAdmins_ThenReturnsBadRequest()
    {
        // Given a name filter longer than Person.Name's 200-character column
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.GetAsync<PaginatedOutput<PersonSummaryOutput>>(
            $"/api/persons/system-admins?pageNumber=1&pageSize=10&name={new string('a', 201)}");

        // Then
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(PaginationMessages.FilterTooLong, response.Body!.Errors);
    }

    [FunctionalFact]
    public async Task GivenScopeAdmin_WhenListSystemAdmins_ThenReturnsForbidden()
    {
        // Given a Scope Admin, whom the role gate excludes: a System Admin's address is not
        // tenant-facing (FR-PE-13)
        Authorize(TestTokens.ForRole((int)Roles.ScopeAdmin));

        // When
        var response = await Gateway.GetAsync<PaginatedOutput<PersonSummaryOutput>>(
            "/api/persons/system-admins?pageNumber=1&pageSize=10");

        // Then
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenPlainUser_WhenListSystemAdmins_ThenReturnsForbidden()
    {
        // Given a User, whom the role gate excludes
        Authorize(TestTokens.ForRole((int)Roles.User));

        // When
        var response = await Gateway.GetAsync<PaginatedOutput<PersonSummaryOutput>>(
            "/api/persons/system-admins?pageNumber=1&pageSize=10");

        // Then
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenUnauthenticatedCaller_WhenListSystemAdmins_ThenReturnsUnauthorized()
    {
        // Given no bearer token
        // When
        var response = await Gateway.GetAsync<PaginatedOutput<PersonSummaryOutput>>(
            "/api/persons/system-admins?pageNumber=1&pageSize=10");

        // Then
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
