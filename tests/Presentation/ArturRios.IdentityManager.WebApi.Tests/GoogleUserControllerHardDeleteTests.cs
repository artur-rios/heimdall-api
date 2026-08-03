using System.Net;
using ArturRios.Configuration.Enums;
using ArturRios.IdentityManager.Command.Output;
using ArturRios.IdentityManager.Domain.Entities;
using ArturRios.IdentityManager.Domain.Enums;
using ArturRios.IdentityManager.Shared.Messages;
using ArturRios.IdentityManager.WebApi.Tests.Support;
using ArturRios.Output;
using ArturRios.Util.Http;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Functional;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.IdentityManager.WebApi.Tests;

// Functional tests for DELETE /api/scopes/{scopeId}/google-users/{id}/hard (UC-29, FR-GO-16): the
// main flow with the row read back as gone, the logically deleted record a cleanup pass starts from,
// every shape of AF-29a (unknown id, wrong scope, repeated call), and the authorization UC-29 leaves
// entirely to the endpoint — a Scope Admin who *owns* the scope is still refused, which is the whole
// difference between this use case and UC-28.
//
// One test confirms the scope survives the deletion: the foreign key points from the Google User to
// the scope, and a cascade in the wrong direction would be catastrophic and silent.
[Collection(nameof(FunctionalCollection))]
public class GoogleUserControllerHardDeleteTests(PostgresFixture db) : WebApiTest<Program>(EnvironmentType.Local)
{
    private static string Route(Guid scopeId, Guid id) => $"/api/scopes/{scopeId}/google-users/{id}/hard";

    private async Task<Scope> SeedScopeAsync()
    {
        await using var context = db.CreateContext();

        var scope = new Scope { PublicId = Guid.NewGuid(), Name = $"scope-{Guid.NewGuid():N}" };

        context.Scopes.Add(scope);
        await context.SaveChangesAsync();

        return scope;
    }

    private async Task<GoogleUser> SeedGoogleUserAsync(Scope scope, bool isDeleted = false)
    {
        await using var context = db.CreateContext();

        var googleUser = new GoogleUser
        {
            PublicId = Guid.NewGuid(),
            GoogleId = $"google-sub-{Guid.NewGuid():N}",
            Name = "Google Signer",
            Email = $"signer-{Guid.NewGuid():N}@gmail.test",
            EmailVerified = true,
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

    private Task<HttpOutput<DataOutput<HardDeleteGoogleUserCommandOutput?>?>> HardDeleteAsync(
        Guid scopeId, Guid id) =>
        Gateway.DeleteAsync<DataOutput<HardDeleteGoogleUserCommandOutput?>>(Route(scopeId, id));

    private async Task<bool> ExistsAsync(Guid publicId)
    {
        await using var context = db.CreateContext();

        return await context.GoogleUsers.AnyAsync(x => x.PublicId == publicId);
    }

    [FunctionalFact]
    public async Task GivenSystemAdmin_WhenHardDelete_ThenRecordIsGone()
    {
        // Given a System Admin, UC-29's only actor (main flow)
        var scope = await SeedScopeAsync();
        var googleUser = await SeedGoogleUserAsync(scope);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await HardDeleteAsync(scope.PublicId, googleUser.PublicId);

        // Then — response
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(GoogleUserMessages.GoogleUserHardDeletedSuccessfully, response.Body!.Messages);
        Assert.Equal(googleUser.PublicId, response.Body.Data!.Id);

        // Then — the row is gone for good (FR-GO-16), not merely flagged
        Assert.False(await ExistsAsync(googleUser.PublicId));
    }

    [FunctionalFact]
    public async Task GivenHardDeletedGoogleUser_WhenReadingTheScopeBack_ThenTheScopeSurvives()
    {
        // Given a Google User removed from a scope. The foreign key points from the Google User to
        // the scope, so nothing should travel the other way — a cascade in that direction would take
        // the scope and everything else in it.
        var scope = await SeedScopeAsync();
        var survivor = await SeedGoogleUserAsync(scope);
        var doomed = await SeedGoogleUserAsync(scope);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        await HardDeleteAsync(scope.PublicId, doomed.PublicId);

        // Then
        await using var context = db.CreateContext();
        Assert.True(await context.Scopes.AnyAsync(x => x.PublicId == scope.PublicId));
        Assert.True(await context.GoogleUsers.AnyAsync(x => x.PublicId == survivor.PublicId));
    }

    [FunctionalFact]
    public async Task GivenLogicallyDeletedGoogleUser_WhenHardDelete_ThenRecordIsGone()
    {
        // Given a Google User UC-28 already soft-deleted — what a cleanup pass starts from
        var scope = await SeedScopeAsync();
        var googleUser = await SeedGoogleUserAsync(scope, isDeleted: true);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await HardDeleteAsync(scope.PublicId, googleUser.PublicId);

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(await ExistsAsync(googleUser.PublicId));
    }

    [FunctionalFact]
    public async Task GivenUnknownId_WhenHardDelete_ThenNotFound()
    {
        // Given an identifier nobody holds (AF-29a)
        var scope = await SeedScopeAsync();
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await HardDeleteAsync(scope.PublicId, Guid.NewGuid());

        // Then
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains(GoogleUserMessages.GoogleUserNotFound, response.Body!.Errors);
    }

    [FunctionalFact]
    public async Task GivenGoogleUserOfAnotherScope_WhenHardDelete_ThenNotFoundAndRecordSurvives()
    {
        // Given a Google User addressed through the wrong scope (AF-29a)
        var scope = await SeedScopeAsync();
        var otherScope = await SeedScopeAsync();
        var googleUser = await SeedGoogleUserAsync(otherScope);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await HardDeleteAsync(scope.PublicId, googleUser.PublicId);

        // Then
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.True(await ExistsAsync(googleUser.PublicId));
    }

    [FunctionalFact]
    public async Task GivenAlreadyHardDeletedGoogleUser_WhenHardDeleteAgain_ThenNotFound()
    {
        // Given a repeat of a call that already succeeded. UC-29 defines no idempotent path, unlike
        // UC-28's AF-28b, so the second call is AF-29a.
        var scope = await SeedScopeAsync();
        var googleUser = await SeedGoogleUserAsync(scope);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));
        Assert.Equal(HttpStatusCode.OK, (await HardDeleteAsync(scope.PublicId, googleUser.PublicId)).StatusCode);

        // When
        var response = await HardDeleteAsync(scope.PublicId, googleUser.PublicId);

        // Then
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains(GoogleUserMessages.GoogleUserNotFound, response.Body!.Errors);
    }

    [FunctionalFact]
    public async Task GivenOwningScopeAdmin_WhenHardDelete_ThenForbiddenAndRecordSurvives()
    {
        // Given a Scope Admin who owns the very scope. This is the whole difference between UC-28 and
        // UC-29: the matrix grants them the logical deletion and withholds the hard one.
        var scope = await SeedScopeAsync();
        var googleUser = await SeedGoogleUserAsync(scope);
        var admin = await SeedScopeAdminAsync(scope);
        Authorize(TestTokens.For(admin.PublicId, (int)Roles.ScopeAdmin, null, scope.PublicId));

        // When
        var response = await HardDeleteAsync(scope.PublicId, googleUser.PublicId);

        // Then
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.True(await ExistsAsync(googleUser.PublicId));
    }

    [FunctionalFact]
    public async Task GivenGoogleUserDeletingThemselves_WhenHardDelete_ThenForbidden()
    {
        // Given the Google User themselves — always User-equivalent (FR-GO-04), so never a System
        // Admin, which is also why UC-29 needs no self-deletion refusal
        var scope = await SeedScopeAsync();
        var googleUser = await SeedGoogleUserAsync(scope);
        Authorize(TestTokens.For(googleUser.PublicId, (int)Roles.User, scope.PublicId));

        // When
        var response = await HardDeleteAsync(scope.PublicId, googleUser.PublicId);

        // Then
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.True(await ExistsAsync(googleUser.PublicId));
    }

    [FunctionalFact]
    public async Task GivenAnonymousCaller_WhenHardDelete_ThenUnauthorized()
    {
        // Given no bearer token
        var scope = await SeedScopeAsync();
        var googleUser = await SeedGoogleUserAsync(scope);

        // When
        var response = await HardDeleteAsync(scope.PublicId, googleUser.PublicId);

        // Then
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.True(await ExistsAsync(googleUser.PublicId));
    }
}
