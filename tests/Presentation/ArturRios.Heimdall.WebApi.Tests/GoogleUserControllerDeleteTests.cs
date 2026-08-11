using System.Net;
using ArturRios.Configuration.Enums;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Query.Output;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Heimdall.WebApi.Tests.Support;
using ArturRios.Output;
using ArturRios.Util.Http;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Functional;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Heimdall.WebApi.Tests;

// Functional tests for DELETE /api/scopes/{scopeId}/google-users/{id} (UC-28, FR-GO-15): the main
// flow with the row read back, AF-28a (unknown id, wrong scope), AF-28b (idempotent repeat that
// writes nothing), AF-28c (non-owning Scope Admin, and both kinds of User at the framework layer),
// and the 401 an anonymous caller gets.
//
// Two tests reach past this endpoint on purpose, because a flag nothing honours is not a deletion:
// one confirms UC-27's default read stops returning the record (FR-GO-17), and one confirms UC-25
// refuses to sign the account back in (AF-25d).
[Collection(nameof(FunctionalCollection))]
public class GoogleUserControllerDeleteTests(PostgresFixture db) : WebApiTest<Program>(EnvironmentType.Local)
{
    private static string Route(Guid scopeId, Guid id) => $"/api/scopes/{scopeId}/google-users/{id}";

    private async Task<Scope> SeedScopeAsync(bool googleSignInEnabled = true)
    {
        await using var context = db.CreateContext();

        var scope = new Scope
        {
            PublicId = Guid.NewGuid(),
            Name = $"scope-{Guid.NewGuid():N}",
            GoogleSignInEnabled = googleSignInEnabled
        };

        context.Scopes.Add(scope);
        await context.SaveChangesAsync();

        return scope;
    }

    private async Task<GoogleUser> SeedGoogleUserAsync(
        Scope scope, bool isDeleted = false, string? googleId = null)
    {
        await using var context = db.CreateContext();

        var googleUser = new GoogleUser
        {
            PublicId = Guid.NewGuid(),
            GoogleId = googleId ?? $"google-sub-{Guid.NewGuid():N}",
            Name = "Google Signer",
            Email = $"signer-{Guid.NewGuid():N}@gmail.test",
            EmailVerified = true,
            ScopeId = scope.Id,
            IsDeleted = isDeleted,
            UpdatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
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

    private Task<HttpOutput<DataOutput<DeleteGoogleUserCommandOutput?>?>> DeleteAsync(Guid scopeId, Guid id) =>
        Gateway.DeleteAsync<DataOutput<DeleteGoogleUserCommandOutput?>>(Route(scopeId, id));

    private async Task<GoogleUser> StoredAsync(Guid publicId)
    {
        await using var context = db.CreateContext();

        return await context.GoogleUsers.FirstAsync(x => x.PublicId == publicId);
    }

    [FunctionalFact]
    public async Task GivenSystemAdminAndActiveGoogleUser_WhenDelete_ThenFlagIsSet()
    {
        // Given a System Admin, who may delete any Google User (UC-28 main flow)
        var scope = await SeedScopeAsync();
        var googleUser = await SeedGoogleUserAsync(scope);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await DeleteAsync(scope.PublicId, googleUser.PublicId);

        // Then — response
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(GoogleUserMessages.GoogleUserDeletedSuccessfully, response.Body!.Messages);
        Assert.Equal(googleUser.PublicId, response.Body.Data!.Id);
        Assert.False(response.Body.Data.AlreadyDeleted);

        // Then — persisted state (FR-GO-15)
        var stored = await StoredAsync(googleUser.PublicId);
        Assert.True(stored.IsDeleted);
        Assert.True(stored.UpdatedAt > googleUser.UpdatedAt);
    }

    [FunctionalFact]
    public async Task GivenOwningScopeAdmin_WhenDelete_ThenFlagIsSet()
    {
        // Given the owner of the Google User's scope (UC-28 step 2)
        var scope = await SeedScopeAsync();
        var googleUser = await SeedGoogleUserAsync(scope);
        var admin = await SeedScopeAdminAsync(scope);
        Authorize(TestTokens.For(admin.PublicId, (int)Roles.ScopeAdmin, null, scope.PublicId));

        // When
        var response = await DeleteAsync(scope.PublicId, googleUser.PublicId);

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True((await StoredAsync(googleUser.PublicId)).IsDeleted);
    }

    [FunctionalFact]
    public async Task GivenDeletedGoogleUser_WhenGetByIdWithoutIncludeDeleted_ThenNotFound()
    {
        // Given a Google User this endpoint just deleted — the flag is only a deletion if the reads
        // honour it (FR-GO-17)
        var scope = await SeedScopeAsync();
        var googleUser = await SeedGoogleUserAsync(scope);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));
        await DeleteAsync(scope.PublicId, googleUser.PublicId);

        // When
        var read = await Gateway.GetAsync<DataOutput<GoogleUserOutput?>>(
            Route(scope.PublicId, googleUser.PublicId));

        // Then
        Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
        Assert.Contains(GoogleUserMessages.GoogleUserNotFound, read.Body!.Errors);
    }

    [FunctionalFact]
    public async Task GivenDeletedGoogleUser_WhenSigningInWithGoogleAgain_ThenUnauthorized()
    {
        // Given an account signed up through UC-25 and then deleted here. UC-25 AF-25d refuses a
        // logically deleted Google User, so the deletion has to actually end the account's access —
        // the same GoogleId must not be able to sign back in.
        var scope = await SeedScopeAsync();
        var subject = $"google-sub-{Guid.NewGuid():N}";
        var idToken = TestGoogleTokens.For(subject);

        var signUp = await Gateway.PostAsync<DataOutput<GoogleSignInCommandOutput?>>(
            "/api/auth/google", new { ScopeId = scope.PublicId, IdToken = idToken });
        Assert.Equal(HttpStatusCode.OK, signUp.StatusCode);

        await using (var context = db.CreateContext())
        {
            var created = await context.GoogleUsers.FirstAsync(x => x.GoogleId == subject);
            Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));
            var deletion = await DeleteAsync(scope.PublicId, created.PublicId);
            Assert.Equal(HttpStatusCode.OK, deletion.StatusCode);
        }

        // When the same Google account presents a fresh, valid ID token
        var again = await Gateway.PostAsync<DataOutput<GoogleSignInCommandOutput?>>(
            "/api/auth/google", new { ScopeId = scope.PublicId, IdToken = TestGoogleTokens.For(subject) });

        // Then — AF-25d, and no duplicate row was created to work around the deleted one
        Assert.Equal(HttpStatusCode.Unauthorized, again.StatusCode);
        Assert.Contains(AuthMessages.GoogleAuthenticationFailed, again.Body!.Errors);

        await using var verify = db.CreateContext();
        Assert.Single(await verify.GoogleUsers.Where(x => x.GoogleId == subject).ToListAsync());
    }

    [FunctionalFact]
    public async Task GivenAlreadyDeletedGoogleUser_WhenDelete_ThenSucceedsIdempotentlyWithoutWriting()
    {
        // Given a record already logically deleted (AF-28b)
        var scope = await SeedScopeAsync();
        var googleUser = await SeedGoogleUserAsync(scope, isDeleted: true);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await DeleteAsync(scope.PublicId, googleUser.PublicId);

        // Then — the same 200 and message as the main flow; the flag distinguishes them
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(GoogleUserMessages.GoogleUserDeletedSuccessfully, response.Body!.Messages);
        Assert.True(response.Body.Data!.AlreadyDeleted);

        // Then — UpdatedAt untouched
        Assert.Equal(googleUser.UpdatedAt, (await StoredAsync(googleUser.PublicId)).UpdatedAt);
    }

    [FunctionalFact]
    public async Task GivenUnknownId_WhenDelete_ThenNotFound()
    {
        // Given an identifier nobody holds (AF-28a)
        var scope = await SeedScopeAsync();
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await DeleteAsync(scope.PublicId, Guid.NewGuid());

        // Then
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains(GoogleUserMessages.GoogleUserNotFound, response.Body!.Errors);
    }

    [FunctionalFact]
    public async Task GivenGoogleUserOfAnotherScope_WhenDelete_ThenNotFoundAndNotDeleted()
    {
        // Given a Google User addressed through the wrong scope (AF-28a)
        var scope = await SeedScopeAsync();
        var otherScope = await SeedScopeAsync();
        var googleUser = await SeedGoogleUserAsync(otherScope);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await DeleteAsync(scope.PublicId, googleUser.PublicId);

        // Then — refused, and untouched: a wrong-scope path must not delete anything
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.False((await StoredAsync(googleUser.PublicId)).IsDeleted);
    }

    [FunctionalFact]
    public async Task GivenNonOwningScopeAdmin_WhenDelete_ThenForbiddenAndNotDeleted()
    {
        // Given a Scope Admin who owns some other scope (AF-28c)
        var scope = await SeedScopeAsync();
        var googleUser = await SeedGoogleUserAsync(scope);
        var otherScope = await SeedScopeAsync();
        var admin = await SeedScopeAdminAsync(otherScope);
        Authorize(TestTokens.For(admin.PublicId, (int)Roles.ScopeAdmin, null, otherScope.PublicId));

        // When
        var response = await DeleteAsync(scope.PublicId, googleUser.PublicId);

        // Then
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains(GoogleUserMessages.NotAuthorizedToDeleteGoogleUser, response.Body!.Errors);
        Assert.False((await StoredAsync(googleUser.PublicId)).IsDeleted);
    }

    [FunctionalFact]
    public async Task GivenGoogleUserDeletingThemselves_WhenDelete_ThenForbidden()
    {
        // Given the Google User themselves. UC-27 lets them read their own record; the matrix grants
        // them no deletion of it, and the RoleRequirement refuses them before the handler runs.
        var scope = await SeedScopeAsync();
        var googleUser = await SeedGoogleUserAsync(scope);
        Authorize(TestTokens.For(googleUser.PublicId, (int)Roles.User, scope.PublicId));

        // When
        var response = await DeleteAsync(scope.PublicId, googleUser.PublicId);

        // Then
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False((await StoredAsync(googleUser.PublicId)).IsDeleted);
    }

    [FunctionalFact]
    public async Task GivenAnonymousCaller_WhenDelete_ThenUnauthorized()
    {
        // Given no bearer token
        var scope = await SeedScopeAsync();
        var googleUser = await SeedGoogleUserAsync(scope);

        // When
        var response = await DeleteAsync(scope.PublicId, googleUser.PublicId);

        // Then
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.False((await StoredAsync(googleUser.PublicId)).IsDeleted);
    }
}
