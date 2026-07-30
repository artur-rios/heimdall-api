using System.Net;
using ArturRios.Configuration.Enums;
using ArturRios.IdentityManager.Command.Input;
using ArturRios.IdentityManager.Command.Output;
using ArturRios.IdentityManager.Domain.Entities;
using ArturRios.IdentityManager.Domain.Enums;
using ArturRios.IdentityManager.Query.Output;
using ArturRios.IdentityManager.Shared.Messages;
using ArturRios.IdentityManager.WebApi.Security;
using ArturRios.IdentityManager.WebApi.Tests.Support;
using ArturRios.Output;
using ArturRios.Util.Hashing;
using ArturRios.Util.Http;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Functional;
using ArturRios.Util.WebApi.Security.Authentication;
using ArturRios.Util.WebApi.Security.Constants;

namespace ArturRios.IdentityManager.WebApi.Tests;

// Functional tests for POST /api/auth/login (UC-11, FR-AU-01…07): the main flow for each of the
// three roles with assertions on the issued token's claims, a round trip proving the token the
// endpoint issues is one the API accepts, AF-11a…AF-11e (401), and AF-11f (400).
[Collection(nameof(FunctionalCollection))]
public class AuthControllerLoginTests(PostgresFixture db) : WebApiTest<Program>(EnvironmentType.Local)
{
    private const string Password = "Str0ng-Login-Pass!";

    private static string UniqueEmail(string prefix) => $"{prefix}-{Guid.NewGuid():N}@test.local";

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

    private async Task<Person> SeedPersonAsync(
        Roles role, string email, bool isDeleted = false, string password = Password)
    {
        await using var context = db.CreateContext();
        var person = new Person
        {
            PublicId = Guid.NewGuid(),
            Name = $"{role}",
            Email = email,
            PasswordHash = Hash.EncodeWithRandomSalt(password, out var salt),
            Salt = salt,
            RoleId = (long)role,
            EmailVerified = true,
            IsDeleted = isDeleted
        };
        context.Persons.Add(person);
        await context.SaveChangesAsync();
        return person;
    }

    private async Task<Person> SeedUserAsync(Scope scope, string email, bool isDeleted = false)
    {
        var person = await SeedPersonAsync(Roles.User, email, isDeleted);

        await using var context = db.CreateContext();
        context.ScopeUsers.Add(new ScopeUser { ScopeId = scope.Id, PersonId = person.Id });
        await context.SaveChangesAsync();

        return person;
    }

    private async Task<Person> SeedScopeAdminAsync(string email, params Scope[] owned)
    {
        var person = await SeedPersonAsync(Roles.ScopeAdmin, email);

        await using var context = db.CreateContext();
        context.ScopeOwners.AddRange(
            owned.Select(scope => new ScopeOwner { ScopeId = scope.Id, PersonId = person.Id }));
        await context.SaveChangesAsync();

        return person;
    }

    private Task<HttpOutput<DataOutput<LoginCommandOutput?>?>> LoginAsync(
        string email, string password = Password, Guid? scopeId = null) =>
        Gateway.PostAsync<DataOutput<LoginCommandOutput?>>(
            "/api/auth/login",
            new LoginCommand { Email = email, Password = password, ScopeId = scopeId });

    /// <summary>Reads the claims out of an issued token, to assert on what FR-AU-04 requires.</summary>
    private static IdentityUser ClaimsOf(string token) =>
        (IdentityUser)new IdentityUserMapper().FromClaims(TokenClaimsReader.Read(token)!)!;

    [FunctionalFact]
    public async Task GivenUserWithScopeIdAndCorrectPassword_WhenPostLogin_ThenTokenCarriesPersonAndScope()
    {
        // Given a User of a live scope
        var scope = await SeedScopeAsync();
        var email = UniqueEmail("user");
        var person = await SeedUserAsync(scope, email);

        // When
        var response = await LoginAsync(email, scopeId: scope.PublicId);

        // Then — response
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(response.Body?.Data?.Token);
        Assert.True(response.Body!.Data!.ExpiresAt > DateTime.UtcNow);
        Assert.Contains(AuthMessages.LoginSuccessful, response.Body.Messages);

        // Then — the token's claims (FR-AU-04)
        var claims = ClaimsOf(response.Body.Data.Token);
        Assert.Equal(person.PublicId, claims.Id);
        Assert.Equal((int)Roles.User, claims.RoleId);
        Assert.Equal(scope.PublicId, claims.ScopeId);
        Assert.Empty(claims.OwnedScopeIds);
    }

    [FunctionalFact]
    public async Task GivenScopeAdminWithoutScopeId_WhenPostLogin_ThenTokenCarriesOwnedScopes()
    {
        // Given a ScopeAdmin owning two live scopes
        var first = await SeedScopeAsync();
        var second = await SeedScopeAsync();
        var email = UniqueEmail("admin");
        var person = await SeedScopeAdminAsync(email, first, second);

        // When
        var response = await LoginAsync(email);

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var claims = ClaimsOf(response.Body!.Data!.Token);
        Assert.Equal(person.PublicId, claims.Id);
        Assert.Null(claims.ScopeId);
        Assert.Equal(new[] { first.PublicId, second.PublicId }.Order(), claims.OwnedScopeIds.Order());
    }

    [FunctionalFact]
    public async Task GivenSystemAdmin_WhenPostLogin_ThenTokenCarriesNoScopeClaim()
    {
        // Given the master System Admin the API seeds on start-up
        // When
        var response = await LoginAsync(PostgresFixture.MasterUserEmail, PostgresFixture.MasterUserPassword);

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var claims = ClaimsOf(response.Body!.Data!.Token);
        Assert.Equal((int)Roles.SystemAdmin, claims.RoleId);
        Assert.Null(claims.ScopeId);
        Assert.Empty(claims.OwnedScopeIds);
    }

    [FunctionalFact]
    public async Task GivenIssuedToken_WhenCallingAuthenticatedEndpoint_ThenRequestIsAuthorized()
    {
        // Given a token obtained by actually logging in — the round trip that proves the claims this
        // API writes are the claims it reads back
        var login = await LoginAsync(PostgresFixture.MasterUserEmail, PostgresFixture.MasterUserPassword);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        var scope = await SeedScopeAsync();
        var person = await SeedUserAsync(scope, UniqueEmail("member"));
        Authorize(login.Body!.Data!.Token);

        // When the token is used on an endpoint that requires authentication
        var response = await Gateway.GetAsync<DataOutput<PersonOutput?>>($"/api/persons/{person.PublicId}");

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(person.PublicId, response.Body?.Data?.Id);
    }

    [FunctionalFact]
    public async Task GivenEmailDifferingInCase_WhenPostLogin_ThenLoginSucceeds()
    {
        // Given a stored email in mixed case
        var email = UniqueEmail("MiXeD").ToUpper();
        await SeedPersonAsync(Roles.SystemAdmin, email);

        // When authenticating with the lower-case form
        var response = await LoginAsync(email.ToLower());

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenUnknownEmail_WhenPostLogin_ThenUnauthorized()
    {
        // Given — AF-11a
        // When
        var response = await LoginAsync(UniqueEmail("nobody"));

        // Then
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(AuthMessages.InvalidCredentials, response.Body!.Errors);
    }

    [FunctionalFact]
    public async Task GivenUserAndWrongScopeId_WhenPostLogin_ThenUnauthorized()
    {
        // Given — AF-11a: the User exists, but in another scope
        var theirScope = await SeedScopeAsync();
        var otherScope = await SeedScopeAsync();
        var email = UniqueEmail("user");
        await SeedUserAsync(theirScope, email);

        // When
        var response = await LoginAsync(email, scopeId: otherScope.PublicId);

        // Then
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenUserEmailWithoutScopeId_WhenPostLogin_ThenUnauthorized()
    {
        // Given — AF-11a: without a scope id the admin lookup runs, which must not reach a User
        var scope = await SeedScopeAsync();
        var email = UniqueEmail("user");
        await SeedUserAsync(scope, email);

        // When
        var response = await LoginAsync(email);

        // Then
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenWrongPassword_WhenPostLogin_ThenUnauthorized()
    {
        // Given — AF-11b
        var email = UniqueEmail("admin");
        await SeedPersonAsync(Roles.SystemAdmin, email);

        // When
        var response = await LoginAsync(email, password: "Wr0ng-Pass!");

        // Then — the same answer as an unknown email, so the two cannot be told apart
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(AuthMessages.InvalidCredentials, response.Body!.Errors);
    }

    [FunctionalFact]
    public async Task GivenLogicallyDeletedPerson_WhenPostLogin_ThenUnauthorized()
    {
        // Given — AF-11c (FR-AU-05): correct credentials, deleted account
        var email = UniqueEmail("deleted");
        await SeedPersonAsync(Roles.SystemAdmin, email, isDeleted: true);

        // When
        var response = await LoginAsync(email);

        // Then
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenUserWhoseScopeIsDeleted_WhenPostLogin_ThenUnauthorized()
    {
        // Given — AF-11d (FR-AU-06)
        var scope = await SeedScopeAsync(isDeleted: true);
        var email = UniqueEmail("user");
        await SeedUserAsync(scope, email);

        // When
        var response = await LoginAsync(email, scopeId: scope.PublicId);

        // Then
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenScopeAdminWhoseScopesAreAllDeleted_WhenPostLogin_ThenUnauthorized()
    {
        // Given — AF-11e (FR-AU-07)
        var first = await SeedScopeAsync(isDeleted: true);
        var second = await SeedScopeAsync(isDeleted: true);
        var email = UniqueEmail("admin");
        await SeedScopeAdminAsync(email, first, second);

        // When
        var response = await LoginAsync(email);

        // Then
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenScopeAdminWithOneLiveScope_WhenPostLogin_ThenOnlyLiveScopeIsClaimed()
    {
        // Given — the AF-11e boundary: one owned scope deleted, one live
        var live = await SeedScopeAsync();
        var deleted = await SeedScopeAsync(isDeleted: true);
        var email = UniqueEmail("admin");
        await SeedScopeAdminAsync(email, deleted, live);

        // When
        var response = await LoginAsync(email);

        // Then the login is admitted and the token claims only the surviving scope
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal([live.PublicId], ClaimsOf(response.Body!.Data!.Token).OwnedScopeIds);
    }

    [FunctionalTheory]
    [InlineData("", Password)]
    [InlineData("not-an-email", Password)]
    [InlineData("someone@test.local", "")]
    public async Task GivenMalformedCredentials_WhenPostLogin_ThenBadRequest(string email, string password)
    {
        // Given — AF-11f
        // When
        var response = await LoginAsync(email, password);

        // Then
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenNoBearerToken_WhenPostLogin_ThenEndpointAnswersAnonymously()
    {
        // Given no bearer token on the gateway: this endpoint is where a caller gets one, so the
        // authentication middleware must let the request through ([AllowAnonymous]). A 200 is the
        // unambiguous proof — an unauthenticated rejection and a rejected login are both 401.
        var response = await LoginAsync(
            PostgresFixture.MasterUserEmail, PostgresFixture.MasterUserPassword);

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(response.Body?.Data?.Token);
    }
}
