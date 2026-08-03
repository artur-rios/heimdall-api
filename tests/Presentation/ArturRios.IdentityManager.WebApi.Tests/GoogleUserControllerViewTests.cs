using System.Net;
using ArturRios.Configuration.Enums;
using ArturRios.IdentityManager.Domain.Entities;
using ArturRios.IdentityManager.Domain.Enums;
using ArturRios.IdentityManager.Query.Output;
using ArturRios.IdentityManager.Shared.Messages;
using ArturRios.IdentityManager.WebApi.Tests.Support;
using ArturRios.Output;
using ArturRios.Util.Http;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Functional;

namespace ArturRios.IdentityManager.WebApi.Tests;

// Functional tests for both UC-27 endpoints (FR-GO-14/FR-GO-17):
//   GET /api/scopes/{scopeId}/google-users        — listing, ScopeAdmin (owner)+
//   GET /api/scopes/{scopeId}/google-users/{id}   — by id, authenticated
//
// The two differ in exactly one way and every authorization test here exists to pin it: a Google
// User may read their own record but may never list a scope. Also covers AF-27a (unknown id, another
// scope's Google User, logically deleted, unknown or deleted scope), AF-27b (non-owning Scope Admin,
// a User at the framework layer on the listing and at the handler on the by-id read), the filters,
// include-deleted, and the 401 both endpoints give an anonymous caller.
[Collection(nameof(FunctionalCollection))]
public class GoogleUserControllerViewTests(PostgresFixture db) : WebApiTest<Program>(EnvironmentType.Local)
{
    private static string ListRoute(Guid scopeId) =>
        $"/api/scopes/{scopeId}/google-users?pageNumber=1&pageSize=10";

    private static string ByIdRoute(Guid scopeId, Guid id) => $"/api/scopes/{scopeId}/google-users/{id}";

    private async Task<Scope> SeedScopeAsync(bool isDeleted = false)
    {
        await using var context = db.CreateContext();

        var scope = new Scope
        {
            PublicId = Guid.NewGuid(), Name = $"scope-{Guid.NewGuid():N}", IsDeleted = isDeleted
        };

        context.Scopes.Add(scope);
        await context.SaveChangesAsync();

        return scope;
    }

    private async Task<GoogleUser> SeedGoogleUserAsync(
        Scope scope, string? name = null, string? email = null, bool isDeleted = false)
    {
        await using var context = db.CreateContext();

        var googleUser = new GoogleUser
        {
            PublicId = Guid.NewGuid(),
            GoogleId = $"google-sub-{Guid.NewGuid():N}",
            Name = name ?? "Google Signer",
            Email = email ?? $"signer-{Guid.NewGuid():N}@gmail.test",
            EmailVerified = true,
            ProfilePictureUrl = "https://lh3.googleusercontent.test/a/photo",
            ScopeId = scope.Id,
            IsDeleted = isDeleted
        };

        context.GoogleUsers.Add(googleUser);
        await context.SaveChangesAsync();

        return googleUser;
    }

    private async Task<Person> SeedScopeAdminAsync(Scope? ownedScope = null)
    {
        await using var context = db.CreateContext();

        var person = new Person
        {
            PublicId = Guid.NewGuid(),
            Name = "Admin",
            Email = $"admin-{Guid.NewGuid():N}@test.local",
            RoleId = (long)Roles.ScopeAdmin,
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

    private Task<HttpOutput<PaginatedOutput<GoogleUserOutput>?>> ListAsync(Guid scopeId, string query = "") =>
        Gateway.GetAsync<PaginatedOutput<GoogleUserOutput>>(ListRoute(scopeId) + query);

    private Task<HttpOutput<DataOutput<GoogleUserOutput?>?>> GetByIdAsync(
        Guid scopeId, Guid id, string query = "") =>
        Gateway.GetAsync<DataOutput<GoogleUserOutput?>>(ByIdRoute(scopeId, id) + query);

    // ----- by id -----

    [FunctionalFact]
    public async Task GivenSystemAdmin_WhenGetGoogleUserById_ThenReturnsEveryRegisteredField()
    {
        // Given a System Admin, who may view any Google User (UC-27 step 2)
        var scope = await SeedScopeAsync();
        var googleUser = await SeedGoogleUserAsync(scope);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await GetByIdAsync(scope.PublicId, googleUser.PublicId);

        // Then — FR-GO-05's fields, addressed by public identifiers only (NFR-15)
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(GoogleUserMessages.GoogleUserRetrievedSuccessfully, response.Body!.Messages);
        Assert.Equal(googleUser.PublicId, response.Body.Data!.Id);
        Assert.Equal(googleUser.GoogleId, response.Body.Data.GoogleId);
        Assert.Equal(googleUser.Name, response.Body.Data.Name);
        Assert.Equal(googleUser.Email, response.Body.Data.Email);
        Assert.True(response.Body.Data.EmailVerified);
        Assert.Equal(googleUser.ProfilePictureUrl, response.Body.Data.ProfilePictureUrl);
        Assert.Equal(scope.PublicId, response.Body.Data.ScopeId);
        Assert.False(response.Body.Data.IsDeleted);
    }

    [FunctionalFact]
    public async Task GivenOwningScopeAdmin_WhenGetGoogleUserById_ThenReturnsIt()
    {
        // Given a Scope Admin who owns the Google User's scope
        var scope = await SeedScopeAsync();
        var googleUser = await SeedGoogleUserAsync(scope);
        var admin = await SeedScopeAdminAsync(scope);
        Authorize(TestTokens.For(admin.PublicId, (int)Roles.ScopeAdmin, null, scope.PublicId));

        // When
        var response = await GetByIdAsync(scope.PublicId, googleUser.PublicId);

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(googleUser.PublicId, response.Body!.Data!.Id);
    }

    [FunctionalFact]
    public async Task GivenGoogleUserReadingThemselves_WhenGetGoogleUserById_ThenReturnsIt()
    {
        // Given the Google User themselves — the actor a RoleRequirement on this endpoint would have
        // locked out, since their token carries the User role (FR-GO-04)
        var scope = await SeedScopeAsync();
        var googleUser = await SeedGoogleUserAsync(scope);
        Authorize(TestTokens.For(googleUser.PublicId, (int)Roles.User, scope.PublicId));

        // When
        var response = await GetByIdAsync(scope.PublicId, googleUser.PublicId);

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(googleUser.PublicId, response.Body!.Data!.Id);
    }

    [FunctionalFact]
    public async Task GivenAnotherGoogleUser_WhenGetGoogleUserById_ThenForbidden()
    {
        // Given one Google User reading another in the same scope — the matrix grants a User this
        // read only as self (AF-27b)
        var scope = await SeedScopeAsync();
        var target = await SeedGoogleUserAsync(scope);
        var caller = await SeedGoogleUserAsync(scope);
        Authorize(TestTokens.For(caller.PublicId, (int)Roles.User, scope.PublicId));

        // When
        var response = await GetByIdAsync(scope.PublicId, target.PublicId);

        // Then
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains(GoogleUserMessages.NotAuthorizedToViewGoogleUser, response.Body!.Errors);
    }

    [FunctionalFact]
    public async Task GivenNonOwningScopeAdmin_WhenGetGoogleUserById_ThenForbidden()
    {
        // Given a Scope Admin who owns some other scope (AF-27b)
        var scope = await SeedScopeAsync();
        var googleUser = await SeedGoogleUserAsync(scope);
        var otherScope = await SeedScopeAsync();
        var admin = await SeedScopeAdminAsync(otherScope);
        Authorize(TestTokens.For(admin.PublicId, (int)Roles.ScopeAdmin, null, otherScope.PublicId));

        // When
        var response = await GetByIdAsync(scope.PublicId, googleUser.PublicId);

        // Then
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains(GoogleUserMessages.NotAuthorizedToViewGoogleUser, response.Body!.Errors);
    }

    [FunctionalFact]
    public async Task GivenUnknownId_WhenGetGoogleUserById_ThenNotFound()
    {
        // Given an identifier nobody holds (AF-27a)
        var scope = await SeedScopeAsync();
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await GetByIdAsync(scope.PublicId, Guid.NewGuid());

        // Then
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains(GoogleUserMessages.GoogleUserNotFound, response.Body!.Errors);
    }

    [FunctionalFact]
    public async Task GivenGoogleUserOfAnotherScope_WhenGetGoogleUserById_ThenNotFound()
    {
        // Given a Google User that exists, addressed through the wrong scope — not the resource this
        // path names, so AF-27a and not AF-27b, even for a System Admin who could read it elsewhere
        var scope = await SeedScopeAsync();
        var otherScope = await SeedScopeAsync();
        var googleUser = await SeedGoogleUserAsync(otherScope);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await GetByIdAsync(scope.PublicId, googleUser.PublicId);

        // Then
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains(GoogleUserMessages.GoogleUserNotFound, response.Body!.Errors);
    }

    [FunctionalFact]
    public async Task GivenLogicallyDeletedGoogleUser_WhenGetGoogleUserById_ThenNotFound()
    {
        // Given a logically deleted record and a default read (FR-GO-17, AF-27a)
        var scope = await SeedScopeAsync();
        var googleUser = await SeedGoogleUserAsync(scope, isDeleted: true);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await GetByIdAsync(scope.PublicId, googleUser.PublicId);

        // Then
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains(GoogleUserMessages.GoogleUserNotFound, response.Body!.Errors);
    }

    [FunctionalFact]
    public async Task GivenLogicallyDeletedGoogleUserAndIncludeDeleted_WhenGetGoogleUserById_ThenReturnsIt()
    {
        // Given the same record, explicitly requested (FR-GO-17's escape hatch)
        var scope = await SeedScopeAsync();
        var googleUser = await SeedGoogleUserAsync(scope, isDeleted: true);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await GetByIdAsync(scope.PublicId, googleUser.PublicId, "?includeDeleted=true");

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Body!.Data!.IsDeleted);
    }

    [FunctionalFact]
    public async Task GivenAnonymousCaller_WhenGetGoogleUserById_ThenUnauthorized()
    {
        // Given no bearer token — the matrix withholds every Google User read from anonymous callers
        var scope = await SeedScopeAsync();
        var googleUser = await SeedGoogleUserAsync(scope);

        // When
        var response = await GetByIdAsync(scope.PublicId, googleUser.PublicId);

        // Then
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ----- listing -----

    [FunctionalFact]
    public async Task GivenSystemAdmin_WhenListingScopeGoogleUsers_ThenReturnsOnlyThatScopesActiveOnes()
    {
        // Given two scopes with Google Users, one of them logically deleted (FR-GO-06/17)
        var scope = await SeedScopeAsync();
        var otherScope = await SeedScopeAsync();
        var alice = await SeedGoogleUserAsync(scope, "Alice");
        var bob = await SeedGoogleUserAsync(scope, "Bob");
        await SeedGoogleUserAsync(scope, "Deleted", isDeleted: true);
        await SeedGoogleUserAsync(otherScope, "Elsewhere");
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await ListAsync(scope.PublicId);

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(GoogleUserMessages.GoogleUsersRetrievedSuccessfully, response.Body!.Messages);
        Assert.Equal([alice.PublicId, bob.PublicId], response.Body.Data!.Select(x => x.Id));
    }

    [FunctionalFact]
    public async Task GivenOwningScopeAdmin_WhenListingScopeGoogleUsers_ThenReturnsThem()
    {
        // Given the owner of the scope
        var scope = await SeedScopeAsync();
        await SeedGoogleUserAsync(scope, "Alice");
        var admin = await SeedScopeAdminAsync(scope);
        Authorize(TestTokens.For(admin.PublicId, (int)Roles.ScopeAdmin, null, scope.PublicId));

        // When
        var response = await ListAsync(scope.PublicId);

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(response.Body!.Data!);
    }

    [FunctionalFact]
    public async Task GivenIncludeDeleted_WhenListingScopeGoogleUsers_ThenReturnsDeletedOnesToo()
    {
        // Given FR-GO-17's escape hatch on the listing
        var scope = await SeedScopeAsync();
        await SeedGoogleUserAsync(scope, "Alice");
        await SeedGoogleUserAsync(scope, "Deleted", isDeleted: true);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await ListAsync(scope.PublicId, "&includeDeleted=true");

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, response.Body!.Data!.Count());
    }

    [FunctionalFact]
    public async Task GivenNameAndEmailFilters_WhenListingScopeGoogleUsers_ThenMatchesCaseInsensitively()
    {
        // Given FR-GO-14's filtering, translated to LOWER() … LIKE by the provider
        var scope = await SeedScopeAsync();
        var alice = await SeedGoogleUserAsync(scope, "Alice Anderson", "alice@gmail.test");
        await SeedGoogleUserAsync(scope, "Bob Brown", "bob@outlook.test");
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var byName = await ListAsync(scope.PublicId, "&name=ANDERSON");
        var byEmail = await ListAsync(scope.PublicId, "&email=GMAIL");

        // Then
        Assert.Equal(alice.PublicId, Assert.Single(byName.Body!.Data!).Id);
        Assert.Equal(alice.PublicId, Assert.Single(byEmail.Body!.Data!).Id);
    }

    [FunctionalFact]
    public async Task GivenUnknownScope_WhenListingScopeGoogleUsers_ThenNotFound()
    {
        // Given a scope identifier nobody holds (AF-27a)
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await ListAsync(Guid.NewGuid());

        // Then
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains(GoogleUserMessages.ScopeNotFound, response.Body!.Errors);
    }

    [FunctionalFact]
    public async Task GivenLogicallyDeletedScope_WhenListingScopeGoogleUsers_ThenNotFound()
    {
        // Given a scope UC-04 logically deleted (AF-27a)
        var scope = await SeedScopeAsync(isDeleted: true);
        await SeedGoogleUserAsync(scope, "Alice");
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await ListAsync(scope.PublicId);

        // Then
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains(GoogleUserMessages.ScopeNotFound, response.Body!.Errors);
    }

    [FunctionalFact]
    public async Task GivenNonOwningScopeAdmin_WhenListingScopeGoogleUsers_ThenForbidden()
    {
        // Given a Scope Admin who owns some other scope (AF-27b)
        var scope = await SeedScopeAsync();
        await SeedGoogleUserAsync(scope, "Alice");
        var otherScope = await SeedScopeAsync();
        var admin = await SeedScopeAdminAsync(otherScope);
        Authorize(TestTokens.For(admin.PublicId, (int)Roles.ScopeAdmin, null, otherScope.PublicId));

        // When
        var response = await ListAsync(scope.PublicId);

        // Then
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains(GoogleUserMessages.NotScopeOwner, response.Body!.Errors);
    }

    [FunctionalFact]
    public async Task GivenGoogleUserOfTheScope_WhenListingScopeGoogleUsers_ThenForbidden()
    {
        // Given a Google User of this very scope. This is the one asymmetry between the two
        // endpoints: they may read themselves by id, but the matrix grants them no listing, and the
        // RoleRequirement refuses them before the handler runs (AF-27b).
        var scope = await SeedScopeAsync();
        var googleUser = await SeedGoogleUserAsync(scope, "Alice");
        Authorize(TestTokens.For(googleUser.PublicId, (int)Roles.User, scope.PublicId));

        // When
        var response = await ListAsync(scope.PublicId);

        // Then
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenAnonymousCaller_WhenListingScopeGoogleUsers_ThenUnauthorized()
    {
        // Given no bearer token
        var scope = await SeedScopeAsync();

        // When
        var response = await ListAsync(scope.PublicId);

        // Then
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
