using System.Net;
using ArturRios.Configuration.Enums;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Output;
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
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Heimdall.WebApi.Tests;

// Functional tests for POST /api/auth/google (UC-25, FR-GO-03…FR-GO-13): both halves of the main
// flow with assertions on the persisted Google User and the issued token's claims, a round trip
// proving the token the endpoint issues is one the API accepts, AF-25a and AF-25d (401), AF-25b
// (403), and AF-25c (409). The host under test verifies ID tokens with LocalGoogleIdTokenVerifier —
// see PostgresFixture — so the tokens are locally signed but still genuinely verified.
[Collection(nameof(FunctionalCollection))]
public class AuthControllerGoogleSignInTests(PostgresFixture db) : WebApiTest<Program>(EnvironmentType.Local)
{
    private static string UniqueEmail(string prefix) => $"{prefix}-{Guid.NewGuid():N}@gmail.test";

    private async Task<Scope> SeedScopeAsync(bool googleSignInEnabled = true, bool isDeleted = false)
    {
        await using var context = db.CreateContext();

        var scope = new Scope
        {
            PublicId = Guid.NewGuid(),
            Name = $"scope-{Guid.NewGuid():N}",
            GoogleSignInEnabled = googleSignInEnabled,
            IsDeleted = isDeleted
        };

        context.Scopes.Add(scope);
        await context.SaveChangesAsync();

        return scope;
    }

    private async Task<GoogleUser> SeedGoogleUserAsync(
        Scope scope, string googleId, string email, bool isDeleted = false, bool emailVerified = true)
    {
        await using var context = db.CreateContext();

        var googleUser = new GoogleUser
        {
            PublicId = Guid.NewGuid(),
            GoogleId = googleId,
            Name = "Existing Signer",
            Email = email,
            EmailVerified = emailVerified,
            ScopeId = scope.Id,
            IsDeleted = isDeleted
        };

        context.GoogleUsers.Add(googleUser);
        await context.SaveChangesAsync();

        return googleUser;
    }

    private async Task SeedUserPersonAsync(Scope scope, string email)
    {
        await using var context = db.CreateContext();

        var person = new Person
        {
            PublicId = Guid.NewGuid(),
            Name = "Password User",
            Email = email,
            PasswordHash = Hash.EncodeWithRandomSalt("Str0ng-Pass!", out var salt),
            Salt = salt,
            RoleId = (long)Roles.User,
            EmailVerified = true
        };

        context.Persons.Add(person);
        await context.SaveChangesAsync();

        context.ScopeUsers.Add(new ScopeUser { ScopeId = scope.Id, PersonId = person.Id });
        // Person.ScopeId mirrors the membership row, as the application writes it — without
        // it the seeded User sits outside the per-scope uniqueness index.
        person.ScopeId = scope.Id;
        await context.SaveChangesAsync();
    }

    private Task<HttpOutput<DataOutput<GoogleSignInCommandOutput?>?>> SignInAsync(
        Guid scopeId, string idToken) =>
        Gateway.PostAsync<DataOutput<GoogleSignInCommandOutput?>>(
            "/api/auth/google",
            new GoogleSignInCommand { ScopeId = scopeId, IdToken = idToken });

    private async Task<List<GoogleUser>> StoredAsync(Scope scope)
    {
        await using var context = db.CreateContext();

        return await context.GoogleUsers.Where(x => x.ScopeId == scope.Id).ToListAsync();
    }

    /// <summary>Reads the claims out of an issued token, to assert on what UC-25 step 8 requires.</summary>
    private static IdentityUser ClaimsOf(string token) =>
        (IdentityUser)new IdentityUserMapper().FromClaims(TokenClaimsReader.Read(token)!)!;

    [FunctionalFact]
    public async Task GivenGoogleSignInEnabledAndUnknownGoogleAccount_WhenPostAuthGoogle_ThenGoogleUserIsCreatedAndTokenReturned()
    {
        // Given a scope with Google sign-in on and a Google account that has never signed in
        // (UC-25 main flow, FR-GO-09)
        var scope = await SeedScopeAsync();
        var subject = $"google-sub-{Guid.NewGuid():N}";
        var email = UniqueEmail("newcomer");

        // When
        var response = await SignInAsync(
            scope.PublicId, TestGoogleTokens.For(subject, email, name: "New Comer"));

        // Then — response
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(response.Body?.Data?.Token);
        Assert.True(response.Body!.Data!.ExpiresAt > DateTime.UtcNow);
        Assert.Contains(AuthMessages.GoogleSignInSuccessful, response.Body.Messages);

        // Then — database state: one Google User, populated from the token's claims (FR-GO-05) and
        // bound to the scope the sign-in named (FR-GO-06)
        var stored = Assert.Single(await StoredAsync(scope));
        Assert.Equal(subject, stored.GoogleId);
        Assert.Equal(email, stored.Email);
        Assert.Equal("New Comer", stored.Name);
        Assert.True(stored.EmailVerified);
        Assert.Equal("https://lh3.googleusercontent.test/a/photo", stored.ProfilePictureUrl);
        Assert.False(stored.IsDeleted);

        // Then — the token's claims (UC-25 step 8, FR-GO-04)
        var claims = ClaimsOf(response.Body.Data.Token);
        Assert.Equal(stored.PublicId, claims.Id);
        Assert.Equal((int)Roles.User, claims.RoleId);
        Assert.Equal(scope.PublicId, claims.ScopeId);
        Assert.Empty(claims.OwnedScopeIds);
    }

    [FunctionalFact]
    public async Task GivenExistingGoogleUser_WhenPostAuthGoogle_ThenTokenIsReturnedAndNoDuplicateIsCreated()
    {
        // Given the Google account already signed up in this scope (FR-GO-10)
        var scope = await SeedScopeAsync();
        var subject = $"google-sub-{Guid.NewGuid():N}";
        var email = UniqueEmail("returning");
        var existing = await SeedGoogleUserAsync(scope, subject, email);

        // When
        var response = await SignInAsync(scope.PublicId, TestGoogleTokens.For(subject, email));

        // Then — response
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(AuthMessages.GoogleSignInSuccessful, response.Body!.Messages);

        // Then — database state: still one row, and the token names it
        var stored = Assert.Single(await StoredAsync(scope));
        Assert.Equal(existing.PublicId, stored.PublicId);
        Assert.Equal(existing.PublicId, ClaimsOf(response.Body.Data!.Token).Id);
    }

    [FunctionalFact]
    public async Task GivenTokenWithoutProfileClaims_WhenPostAuthGoogle_ThenGoogleUserIsCreatedWithEmptyName()
    {
        // Given a token whose issuer withheld the profile claims — the optional fields stay empty
        // and the account is still created (FR-GO-05)
        var scope = await SeedScopeAsync();

        // When
        var response = await SignInAsync(
            scope.PublicId, TestGoogleTokens.For(name: null, pictureUrl: null));

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var stored = Assert.Single(await StoredAsync(scope));
        Assert.Equal(string.Empty, stored.Name);
        Assert.Null(stored.ProfilePictureUrl);
    }

    [FunctionalFact]
    public async Task GivenIssuedToken_WhenCallingAuthenticatedEndpoint_ThenTokenIsAccepted()
    {
        // Given a Google User who has just signed in — the token the endpoint issues must be one the
        // API reads back, as UC-11's suite proves for a password login
        var scope = await SeedScopeAsync();
        var signIn = await SignInAsync(scope.PublicId, TestGoogleTokens.For());
        Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);

        // When — a scope read, which a User of the scope is allowed to make
        Authorize(signIn.Body!.Data!.Token);
        var response = await Gateway.GetAsync<DataOutput<ScopeOutput>>($"/api/scopes/{scope.PublicId}");

        // Then — accepted, not 401
        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenForgedIdToken_WhenPostAuthGoogle_ThenReturnsUnauthorized()
    {
        // Given a token signed with someone else's key (AF-25a, FR-GO-11)
        var scope = await SeedScopeAsync();

        // When
        var response = await SignInAsync(scope.PublicId, TestGoogleTokens.SignedWithWrongSecret());

        // Then
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(AuthMessages.GoogleAuthenticationFailed, response.Body!.Errors);
        Assert.Empty(await StoredAsync(scope));
    }

    [FunctionalFact]
    public async Task GivenExpiredIdToken_WhenPostAuthGoogle_ThenReturnsUnauthorized()
    {
        // Given a correctly signed token whose lifetime has run out (AF-25a)
        var scope = await SeedScopeAsync();

        // When
        var response = await SignInAsync(
            scope.PublicId, TestGoogleTokens.For(expiresIn: TimeSpan.FromMinutes(-5)));

        // Then
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(AuthMessages.GoogleAuthenticationFailed, response.Body!.Errors);
        Assert.Empty(await StoredAsync(scope));
    }

    [FunctionalFact]
    public async Task GivenMissingIdToken_WhenPostAuthGoogle_ThenReturnsUnauthorized()
    {
        // Given no token at all — UC-25 defines no 400 flow, and needs none: an absent token simply
        // fails verification (AF-25a)
        var scope = await SeedScopeAsync();

        // When
        var response = await SignInAsync(scope.PublicId, string.Empty);

        // Then
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(AuthMessages.GoogleAuthenticationFailed, response.Body!.Errors);
    }

    [FunctionalFact]
    public async Task GivenUnknownScope_WhenPostAuthGoogle_ThenReturnsForbidden()
    {
        // Given a scope identifier matching nothing (AF-25b)
        // When
        var response = await SignInAsync(Guid.NewGuid(), TestGoogleTokens.For());

        // Then — 403, not 404: the endpoint is anonymous, so it refuses without saying whether the
        // scope exists
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains(AuthMessages.GoogleSignInUnavailable, response.Body!.Errors);
    }

    [FunctionalFact]
    public async Task GivenLogicallyDeletedScope_WhenPostAuthGoogle_ThenReturnsForbidden()
    {
        // Given the scope is logically deleted (AF-25b, FR-GO-13)
        var scope = await SeedScopeAsync(isDeleted: true);

        // When
        var response = await SignInAsync(scope.PublicId, TestGoogleTokens.For());

        // Then
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains(AuthMessages.GoogleSignInUnavailable, response.Body!.Errors);
        Assert.Empty(await StoredAsync(scope));
    }

    [FunctionalFact]
    public async Task GivenGoogleSignInDisabled_WhenPostAuthGoogle_ThenReturnsForbidden()
    {
        // Given an active scope that UC-24 never switched the setting on for (AF-25b, FR-GO-03)
        var scope = await SeedScopeAsync(googleSignInEnabled: false);

        // When
        var response = await SignInAsync(scope.PublicId, TestGoogleTokens.For());

        // Then
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains(AuthMessages.GoogleSignInUnavailable, response.Body!.Errors);
        Assert.Empty(await StoredAsync(scope));
    }

    [FunctionalFact]
    public async Task GivenEmailHeldByALogicallyDeletedGoogleUser_WhenPostAuthGoogle_ThenReturnsConflict()
    {
        // Given the address is held in this scope by a Google User that UC-28 logically deleted, and
        // a different Google account now signing up on it.
        //
        // The AF-25c check used to exclude deleted rows while the unique index on
        // (scope_id, lower(email)) does not, so this answered "address is free" and then failed on
        // the insert — a persistence error in place of the 409, for a caller who could never get
        // past it. The check now matches the index.
        var scope = await SeedScopeAsync();
        var email = UniqueEmail("deleted-holder");
        await SeedGoogleUserAsync(scope, $"google-sub-{Guid.NewGuid():N}", email, isDeleted: true);

        // When
        var response = await SignInAsync(scope.PublicId, TestGoogleTokens.For(email: email));

        // Then — a clean 409, and no second row
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains(AuthMessages.EmailAlreadyExists, response.Body!.Errors);
        Assert.Single(await StoredAsync(scope));
    }

    [FunctionalFact]
    public async Task GivenEmailDifferingOnlyByCase_WhenPostAuthGoogle_ThenReturnsConflict()
    {
        // Given the same address in different casing. The application compares with LOWER(), so the
        // index has to as well — over the raw column, "Taken@x" and "taken@x" were two free
        // addresses to the database and one taken address to the handler.
        var scope = await SeedScopeAsync();
        var email = UniqueEmail("cased");
        await SeedGoogleUserAsync(scope, $"google-sub-{Guid.NewGuid():N}", email.ToUpperInvariant());

        // When
        var response = await SignInAsync(scope.PublicId, TestGoogleTokens.For(email: email));

        // Then
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains(AuthMessages.EmailAlreadyExists, response.Body!.Errors);
        Assert.Single(await StoredAsync(scope));
    }

    [FunctionalFact]
    public async Task GivenEmailAlreadyUsedByAnotherGoogleUserInScope_WhenPostAuthGoogle_ThenReturnsConflict()
    {
        // Given the address is held in this scope by a Google User with a different 'sub'
        // (AF-25c, FR-GO-07)
        var scope = await SeedScopeAsync();
        var email = UniqueEmail("taken");
        await SeedGoogleUserAsync(scope, $"google-sub-{Guid.NewGuid():N}", email);

        // When
        var response = await SignInAsync(scope.PublicId, TestGoogleTokens.For(email: email));

        // Then — refused, and no second row was written
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains(AuthMessages.EmailAlreadyExists, response.Body!.Errors);
        Assert.Single(await StoredAsync(scope));
    }

    [FunctionalFact]
    public async Task GivenEmailAlreadyUsedByUserPersonInScope_WhenPostAuthGoogle_ThenReturnsConflict()
    {
        // Given the address belongs to a password-authenticated User of the same scope — the half of
        // FR-GO-07 that spans two tables, so no unique index can enforce it (AF-25c)
        var scope = await SeedScopeAsync();
        var email = UniqueEmail("person");
        await SeedUserPersonAsync(scope, email);

        // When
        var response = await SignInAsync(scope.PublicId, TestGoogleTokens.For(email: email));

        // Then
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains(AuthMessages.EmailAlreadyExists, response.Body!.Errors);
        Assert.Empty(await StoredAsync(scope));
    }

    /// <summary>
    ///     Seeds an admin person: no scope membership, which is how UC-06 path b creates one.
    /// </summary>
    private async Task<Person> SeedAdminPersonAsync(Roles role, string email)
    {
        await using var context = db.CreateContext();

        var person = new Person
        {
            PublicId = Guid.NewGuid(),
            Name = $"{role}",
            Email = email,
            PasswordHash = Hash.EncodeWithRandomSalt("Str0ng-Pass!", out var salt),
            Salt = salt,
            RoleId = (long)role,
            EmailVerified = true
        };

        context.Persons.Add(person);
        await context.SaveChangesAsync();

        return person;
    }

    [FunctionalFact]
    public async Task GivenEmailAlreadyUsedByAnAdminPerson_WhenPostAuthGoogle_ThenASeparateGoogleUserIsCreated()
    {
        // Threat Model TH-21, established rather than assumed. AF-25c asks whether the address is
        // free "within the scope", and it asks the person half of that question only of persons who
        // hold a SCOPE_USER row. An admin has none — UC-06 path b creates them with no scope
        // association at all — so an admin's address does not read as taken, and the sign-up
        // proceeds.
        //
        // That is not the account takeover it might look like, and this test exists to say which of
        // the two it is. What gets created is a new row in the other identity table, with its own
        // PublicId; the token names that row and claims the User role, which UC-25 issues
        // unconditionally. The admin's person row is untouched and the caller receives none of its
        // authority. What is real is that the address now exists twice across the two tables, which
        // is narrower than FR-GO-07 reads.
        var scope = await SeedScopeAsync();
        var email = UniqueEmail("sysadmin");
        var admin = await SeedAdminPersonAsync(Roles.SystemAdmin, email);

        // When somebody signs in with Google using that same address
        var response = await SignInAsync(scope.PublicId, TestGoogleTokens.For(email: email));

        // Then — it is allowed, and what was created is a Google User, not a claim on the admin
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var created = Assert.Single(await StoredAsync(scope));
        Assert.Equal(email, created.Email);
        Assert.NotEqual(admin.PublicId, created.PublicId);

        // Then — the admin's own record is untouched, and still a System Admin
        await using var context = db.CreateContext();
        var storedAdmin = await context.Persons.AsNoTracking()
            .SingleAsync(person => person.PublicId == admin.PublicId);

        Assert.Equal((long)Roles.SystemAdmin, storedAdmin.RoleId);
        Assert.False(storedAdmin.IsDeleted);
    }

    [FunctionalFact]
    public async Task GivenEmailAlreadyUsedByAnAdminPerson_WhenPostAuthGoogle_ThenTheTokenCarriesOnlyTheUserRole()
    {
        // The half of TH-21 that would matter if it were wrong: the token minted for the colliding
        // address must name the Google User and the User role, never the admin it shares an address
        // with. UC-25 hard-codes Roles.User, and ActorLivenessFilter refuses a Google identity
        // claiming anything else — this pins the outcome from the caller's side.
        var scope = await SeedScopeAsync();
        var email = UniqueEmail("scopeadmin");
        var admin = await SeedAdminPersonAsync(Roles.ScopeAdmin, email);

        var response = await SignInAsync(scope.PublicId, TestGoogleTokens.For(email: email));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // The issued token is spendable, and spends as a User of this scope rather than as an admin:
        // an endpoint the seeded ScopeAdmin could reach is refused to its holder.
        Authorize(response.Body!.Data!.Token!);

        var asAdmin = await Gateway.GetAsync<DataOutput<object?>>("/api/scopes");

        Assert.Equal(HttpStatusCode.Forbidden, asAdmin.StatusCode);

        // And the admin's record is still theirs
        await using var context = db.CreateContext();
        Assert.Equal(
            (long)Roles.ScopeAdmin,
            (await context.Persons.AsNoTracking().SingleAsync(p => p.PublicId == admin.PublicId)).RoleId);
    }

    [FunctionalFact]
    public async Task GivenSameEmailInAnotherScope_WhenPostAuthGoogle_ThenGoogleUserIsCreated()
    {
        // Given the address is held in a different scope — FR-GO-07 is per-scope, so it does not
        // collide here
        var scope = await SeedScopeAsync();
        var otherScope = await SeedScopeAsync();
        var email = UniqueEmail("shared");
        await SeedGoogleUserAsync(otherScope, $"google-sub-{Guid.NewGuid():N}", email);

        // When
        var response = await SignInAsync(scope.PublicId, TestGoogleTokens.For(email: email));

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(email, Assert.Single(await StoredAsync(scope)).Email);
    }

    [FunctionalFact]
    public async Task GivenLogicallyDeletedGoogleUser_WhenPostAuthGoogle_ThenReturnsUnauthorized()
    {
        // Given the account signed up before and was logically deleted since (AF-25d, FR-GO-12)
        var scope = await SeedScopeAsync();
        var subject = $"google-sub-{Guid.NewGuid():N}";
        var email = UniqueEmail("deleted");
        await SeedGoogleUserAsync(scope, subject, email, isDeleted: true);

        // When
        var response = await SignInAsync(scope.PublicId, TestGoogleTokens.For(subject, email));

        // Then — refused with the same message a forged token gets, and the row is neither revived
        // nor duplicated
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(AuthMessages.GoogleAuthenticationFailed, response.Body!.Errors);
        var stored = Assert.Single(await StoredAsync(scope));
        Assert.True(stored.IsDeleted);
    }

    [FunctionalFact]
    public async Task GivenTokenReportsUnverifiedAddress_WhenPostAuthGoogle_ThenResponseReportsEmailVerifiedFalse()
    {
        // Given a first sign-in with a Google token whose email_verified claim is false (FR-EV-05)
        var scope = await SeedScopeAsync();

        // When
        var response = await SignInAsync(
            scope.PublicId, TestGoogleTokens.For(emailVerified: false));

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Body!.Data!.EmailVerified);
    }

    [FunctionalFact]
    public async Task GivenStoredValueIsVerifiedAndTokenSaysOtherwise_WhenPostAuthGoogle_ThenStoredValueIsDowngraded()
    {
        // Given the refresh running in the direction its unit test can only assert against a mock:
        // a stored true, and a token that actively asserts the address is not verified. FR-GO-19 is
        // "match the token", so this must reach the database — unlike an absent claim, which asserts
        // nothing and leaves the row alone.
        var scope = await SeedScopeAsync();
        var subject = $"google-sub-{Guid.NewGuid():N}";
        var email = UniqueEmail("downgrade");
        await SeedGoogleUserAsync(scope, subject, email, emailVerified: true);

        // When
        var response = await SignInAsync(
            scope.PublicId, TestGoogleTokens.For(subject, email, emailVerified: false));

        // Then — the response and the persisted row both report the token's value
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Body!.Data!.EmailVerified);
        var stored = Assert.Single(await StoredAsync(scope));
        Assert.False(stored.EmailVerified);
    }

    [FunctionalFact]
    public async Task GivenStoredValueIsStale_WhenPostAuthGoogle_ThenStoredValueIsRefreshed()
    {
        // Given a Google User registered while their address was unverified, signing in again with
        // a token that now says verified (FR-GO-19)
        var scope = await SeedScopeAsync();
        var subject = $"google-sub-{Guid.NewGuid():N}";
        var email = UniqueEmail("stale");
        await SeedGoogleUserAsync(scope, subject, email, emailVerified: false);

        // When
        var response = await SignInAsync(
            scope.PublicId, TestGoogleTokens.For(subject, email, emailVerified: true));

        // Then — the response and the persisted row both report the token's value
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Body!.Data!.EmailVerified);
        var stored = Assert.Single(await StoredAsync(scope));
        Assert.True(stored.EmailVerified);
    }

    [FunctionalFact]
    public async Task GivenTokenOmitsEmailVerifiedClaim_WhenPostAuthGoogle_ThenStoredVerifiedValueIsKept()
    {
        // Given a verified Google User signing in again with a token that carries no email_verified
        // claim at all — what a client that asked for a token without the email scope presents. An
        // absent claim asserts nothing, so FR-GO-19's refresh must not downgrade the stored value
        var scope = await SeedScopeAsync();
        var subject = $"google-sub-{Guid.NewGuid():N}";
        var email = UniqueEmail("silent-claim");
        await SeedGoogleUserAsync(scope, subject, email, emailVerified: true);

        // When
        var response = await SignInAsync(
            scope.PublicId, TestGoogleTokens.For(subject, email, emailVerified: null));

        // Then — the row and the response both still say verified
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Body!.Data!.EmailVerified);
        var stored = Assert.Single(await StoredAsync(scope));
        Assert.True(stored.EmailVerified);
    }

    [FunctionalFact]
    public async Task GivenTokenEmailVerifiedClaimIsNull_WhenPostAuthGoogle_ThenStoredVerifiedValueIsKept()
    {
        // Given a verified Google User signing in again with a token whose email_verified claim is
        // present but JSON null. It asserts no more than an omitted claim does, so FR-GO-19's
        // refresh must leave the stored value alone here too rather than read the null as "false"
        var scope = await SeedScopeAsync();
        var subject = $"google-sub-{Guid.NewGuid():N}";
        var email = UniqueEmail("null-claim");
        await SeedGoogleUserAsync(scope, subject, email, emailVerified: true);

        // When
        var response = await SignInAsync(
            scope.PublicId, TestGoogleTokens.WithNullEmailVerified(subject, email));

        // Then — the row and the response both still say verified
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Body!.Data!.EmailVerified);
        var stored = Assert.Single(await StoredAsync(scope));
        Assert.True(stored.EmailVerified);
    }
}
