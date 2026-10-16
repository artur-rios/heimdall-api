using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using ArturRios.Configuration.Enums;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Query.Output;
using ArturRios.Heimdall.WebApi.Tests.Support;
using ArturRios.Output;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Functional;

namespace ArturRios.Heimdall.WebApi.Tests;

// Functional tests for FR-PE-19 across every endpoint that projects a person's scope ownerships
// (UC-07 reads a, b and c, UC-08) or could (UC-07 read d): a Scope Admin reading somebody else sees
// only the ownerships of scopes they own too; a System Admin and the person themselves see them all.
//
// Two Scope Admins share scope A. The other one also owns scope B, which the reader does not.
[Collection(nameof(FunctionalCollection))]
public class OwnedScopeVisibilityTests(PostgresFixture db) : WebApiTest<Program>(EnvironmentType.Local)
{
    private sealed record Tenancy(Scope Shared, Scope Elsewhere, Person Reader, Person CoOwner);

    private async Task<Scope> SeedScopeAsync()
    {
        await using var context = db.CreateContext();
        var scope = new Scope { PublicId = Guid.NewGuid(), Name = $"scope-{Guid.NewGuid():N}" };
        context.Scopes.Add(scope);
        await context.SaveChangesAsync();
        return scope;
    }

    private async Task<Person> SeedPersonAsync(Roles role, Scope? memberOf, params Scope[] owns)
    {
        await using var context = db.CreateContext();
        var person = new Person
        {
            PublicId = Guid.NewGuid(),
            Name = $"{role}-{Guid.NewGuid():N}",
            Email = $"{role.ToString().ToLowerInvariant()}-{Guid.NewGuid():N}@test.local",
            RoleId = (long)role,
            EmailVerified = true
        };
        context.Persons.Add(person);
        await context.SaveChangesAsync();

        if (memberOf is not null)
        {
            context.ScopeUsers.Add(new ScopeUser { ScopeId = memberOf.Id, PersonId = person.Id });
            person.ScopeId = memberOf.Id;
        }

        foreach (var scope in owns)
        {
            context.ScopeOwners.Add(new ScopeOwner { ScopeId = scope.Id, PersonId = person.Id });
        }

        await context.SaveChangesAsync();

        return person;
    }

    private async Task<Tenancy> SeedTenancyAsync()
    {
        var shared = await SeedScopeAsync();
        var elsewhere = await SeedScopeAsync();
        var reader = await SeedPersonAsync(Roles.ScopeAdmin, null, shared);
        var coOwner = await SeedPersonAsync(Roles.ScopeAdmin, null, shared, elsewhere);

        return new Tenancy(shared, elsewhere, reader, coOwner);
    }

    [FunctionalFact]
    public async Task GivenACoOwnerWhoOwnsAnotherTenantsScope_WhenAScopeAdminGetsThem_ThenOnlyTheSharedScopeIsListed()
    {
        // Given
        var tenancy = await SeedTenancyAsync();
        Authorize(TestTokens.For(tenancy.Reader.PublicId, (int)Roles.ScopeAdmin));

        // When
        var response = await Gateway.GetAsync<DataOutput<PersonOutput?>>($"/api/persons/{tenancy.CoOwner.PublicId}");

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new[] { tenancy.Shared.PublicId }, response.Body!.Data!.OwnedScopeIds);
    }

    [FunctionalFact]
    public async Task GivenACoOwnerWhoOwnsAnotherTenantsScope_WhenASystemAdminGetsThem_ThenEveryScopeIsListed()
    {
        // Given
        var tenancy = await SeedTenancyAsync();
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.GetAsync<DataOutput<PersonOutput?>>($"/api/persons/{tenancy.CoOwner.PublicId}");

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equivalent(
            new[] { tenancy.Shared.PublicId, tenancy.Elsewhere.PublicId }, response.Body!.Data!.OwnedScopeIds, strict: true);
    }

    [FunctionalFact]
    public async Task GivenAScopeAdminOwningTwoScopes_WhenTheyGetThemselves_ThenEveryScopeIsListed()
    {
        // Given — the co-owner reads their own record
        var tenancy = await SeedTenancyAsync();
        Authorize(TestTokens.For(tenancy.CoOwner.PublicId, (int)Roles.ScopeAdmin));

        // When
        var response = await Gateway.GetAsync<DataOutput<PersonOutput?>>($"/api/persons/{tenancy.CoOwner.PublicId}");

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equivalent(
            new[] { tenancy.Shared.PublicId, tenancy.Elsewhere.PublicId }, response.Body!.Data!.OwnedScopeIds, strict: true);
    }

    [FunctionalFact]
    public async Task GivenACoOwnerWhoOwnsAnotherTenantsScope_WhenAScopeAdminListsTheOwners_ThenOnlyTheSharedScopeIsListed()
    {
        // Given
        var tenancy = await SeedTenancyAsync();
        Authorize(TestTokens.For(tenancy.Reader.PublicId, (int)Roles.ScopeAdmin));

        // When
        var response = await Gateway.GetAsync<PaginatedOutput<PersonOutput>>(
            $"/api/scopes/{tenancy.Shared.PublicId}/owners?pageNumber=1&pageSize=10");

        // Then — both owners are listed, each with the shared scope alone
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, response.Body!.Data!.Count());
        Assert.All(response.Body.Data!, owner => Assert.Equal(new[] { tenancy.Shared.PublicId }, owner.OwnedScopeIds));
    }

    [FunctionalFact]
    public async Task GivenACoOwnerWhoOwnsAnotherTenantsScope_WhenASystemAdminListsTheOwners_ThenEveryScopeIsListed()
    {
        // Given
        var tenancy = await SeedTenancyAsync();
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.GetAsync<PaginatedOutput<PersonOutput>>(
            $"/api/scopes/{tenancy.Shared.PublicId}/owners?pageNumber=1&pageSize=10");

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var coOwner = Assert.Single(response.Body!.Data!, owner => owner.Id == tenancy.CoOwner.PublicId);
        Assert.Equivalent(new[] { tenancy.Shared.PublicId, tenancy.Elsewhere.PublicId }, coOwner.OwnedScopeIds, strict: true);
    }

    [FunctionalFact]
    public async Task GivenAUserRecordedAsOwningAnotherScope_WhenAScopeAdminListsTheScopesUsers_ThenNoOwnershipIsListed()
    {
        // Given — no endpoint makes a User an owner (FR-SC-08); the row is written directly, so the
        // listing is shown to trim whatever the data holds
        var tenancy = await SeedTenancyAsync();
        var user = await SeedPersonAsync(Roles.User, tenancy.Shared, tenancy.Elsewhere);
        Authorize(TestTokens.For(tenancy.Reader.PublicId, (int)Roles.ScopeAdmin));

        // When
        var response = await Gateway.GetAsync<PaginatedOutput<PersonOutput>>(
            $"/api/scopes/{tenancy.Shared.PublicId}/persons?pageNumber=1&pageSize=10");

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var listed = Assert.Single(response.Body!.Data!, person => person.Id == user.PublicId);
        Assert.Empty(listed.OwnedScopeIds);
    }

    [FunctionalFact]
    public async Task GivenAUserRecordedAsOwningAnotherScope_WhenASystemAdminListsTheScopesUsers_ThenTheOwnershipIsListed()
    {
        // Given
        var tenancy = await SeedTenancyAsync();
        var user = await SeedPersonAsync(Roles.User, tenancy.Shared, tenancy.Elsewhere);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.GetAsync<PaginatedOutput<PersonOutput>>(
            $"/api/scopes/{tenancy.Shared.PublicId}/persons?pageNumber=1&pageSize=10");

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var listed = Assert.Single(response.Body!.Data!, person => person.Id == user.PublicId);
        Assert.Equal(new[] { tenancy.Elsewhere.PublicId }, listed.OwnedScopeIds);
    }

    [FunctionalFact]
    public async Task GivenAUserRecordedAsOwningAnotherScope_WhenTheScopesAdminUpdatesThem_ThenNoOwnershipIsReturned()
    {
        // Given — as above, the ownership row is written directly
        var tenancy = await SeedTenancyAsync();
        var user = await SeedPersonAsync(Roles.User, tenancy.Shared, tenancy.Elsewhere);
        Authorize(TestTokens.For(tenancy.Reader.PublicId, (int)Roles.ScopeAdmin));

        // When
        var response = await Gateway.PutAsync<DataOutput<UpdatePersonCommandOutput?>>(
            $"/api/persons/{user.PublicId}", new UpdatePersonCommand { Name = "Renamed", Email = user.Email });

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(response.Body!.Data!.OwnedScopeIds);
    }

    [FunctionalFact]
    public async Task GivenAScopeAdminOwningTwoScopes_WhenASystemAdminUpdatesThem_ThenEveryScopeIsReturned()
    {
        // Given
        var tenancy = await SeedTenancyAsync();
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.PutAsync<DataOutput<UpdatePersonCommandOutput?>>(
            $"/api/persons/{tenancy.CoOwner.PublicId}",
            new UpdatePersonCommand { Name = "Renamed", Email = tenancy.CoOwner.Email });

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equivalent(
            new[] { tenancy.Shared.PublicId, tenancy.Elsewhere.PublicId }, response.Body!.Data!.OwnedScopeIds, strict: true);
    }

    [FunctionalFact]
    public async Task GivenACoOwnerWhoOwnsAnotherTenantsScope_WhenAScopeAdminListsTheScopeAdmins_ThenNoOwnershipOrRoleIsInTheResponse()
    {
        // Given
        var tenancy = await SeedTenancyAsync();

        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"/api/persons/scope-admins?email={tenancy.CoOwner.Email}&pageNumber=1&pageSize=10");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer", TestTokens.For(tenancy.Reader.PublicId, (int)Roles.ScopeAdmin));

        // When
        using var response = await Gateway.Client.SendAsync(request);

        // Then — the projection is identifier, name and email (FR-PE-12), and nothing else
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var listed = Assert.Single(document.RootElement.GetProperty("data").EnumerateArray());
        Assert.Equal(
            new[] { "email", "id", "name" },
            listed.EnumerateObject().Select(property => property.Name.ToLowerInvariant()).Order().ToArray());
    }
}
