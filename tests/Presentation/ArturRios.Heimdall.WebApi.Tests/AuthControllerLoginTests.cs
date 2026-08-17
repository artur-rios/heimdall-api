using System.Net;
using ArturRios.Configuration.Enums;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Command.Services;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Query.Output;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Heimdall.WebApi.Security;
using ArturRios.Heimdall.WebApi.Tests.Support;
using ArturRios.Output;
using ArturRios.Util.Hashing;
using ArturRios.Util.Http;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Functional;
using ArturRios.Util.WebApi.Security.Authentication;
using ArturRios.Util.WebApi.Security.Constants;

namespace ArturRios.Heimdall.WebApi.Tests;

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
        Roles role, string email, bool isDeleted = false, string password = Password,
        bool emailVerified = true)
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
            EmailVerified = emailVerified,
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
        // Person.ScopeId mirrors the membership row, as the application writes it — without
        // it the seeded User sits outside the per-scope uniqueness index.
        person.ScopeId = scope.Id;
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
        var claims = ClaimsOf(response.Body.Data.Token!);
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

        var claims = ClaimsOf(response.Body!.Data!.Token!);
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

        var claims = ClaimsOf(response.Body!.Data!.Token!);
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
        Authorize(login.Body!.Data!.Token!);

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
        Assert.Equal([live.PublicId], ClaimsOf(response.Body!.Data!.Token!).OwnedScopeIds);
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

    [FunctionalFact]
    public async Task GivenUnverifiedPerson_WhenPostAuthLogin_ThenResponseReportsEmailVerifiedFalse()
    {
        // Given an admin who never confirmed their address (FR-EV-05)
        var person = await SeedPersonAsync(
            Roles.SystemAdmin, UniqueEmail("unverified"), emailVerified: false);

        // When
        var response = await LoginAsync(person.Email);

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Body!.Data!.EmailVerified);
    }

    [FunctionalFact]
    public async Task GivenVerifiedPerson_WhenPostAuthLogin_ThenResponseReportsEmailVerifiedTrue()
    {
        // Given an admin who confirmed their address
        var person = await SeedPersonAsync(Roles.SystemAdmin, UniqueEmail("verified"));

        // When
        var response = await LoginAsync(person.Email);

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Body!.Data!.EmailVerified);
    }

    [FunctionalFact]
    public async Task GivenTheHashGateIsSaturated_WhenPostLogin_ThenServiceUnavailableRatherThanServerError()
    {
        // Threat Model TH-03, end to end. The unit tests prove the gate counts and refuses; this
        // proves it is wired into the request path and that its refusal reaches the caller as a load
        // condition rather than as a fault. Without the exception filter this answers 500, which is
        // both wrong and the shape an operator would page on.
        //
        // The gate is static, and the host under test runs in this process, so configuring it here
        // configures the one the API uses. Functional tests share a collection and so run one at a
        // time; the original bound is restored either way.
        var email = UniqueEmail("saturated");
        await SeedPersonAsync(Roles.SystemAdmin, email);

        var release = new TaskCompletionSource();
        var holding = new TaskCompletionSource();

        var original = PasswordHashGate.Shared;
        var saturated = new PasswordHashGate(1, TimeSpan.FromMilliseconds(100));

        PasswordHashGate.Shared = saturated;

        var holder = saturated.RunAsync(async () =>
        {
            holding.SetResult();

            await release.Task;
        });

        try
        {
            await holding.Task;

            // When the only permit is taken
            var response = await LoginAsync(email);

            // Then
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Contains(AuthMessages.AuthenticationTemporarilyUnavailable, response.Body!.Errors);
        }
        finally
        {
            release.SetResult();
            await holder;

            PasswordHashGate.Shared = original;
        }

        // Then — and the endpoint works again once the gate drains, so saturation is not a latch
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(email)).StatusCode);
    }
}
