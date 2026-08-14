using System.Net;
using ArturRios.Configuration.Enums;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Heimdall.WebApi.Tests.Support;
using ArturRios.Output;
using ArturRios.Util.Http;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Functional;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Heimdall.WebApi.Tests;

// Functional tests for PUT /api/scopes/{id}/google-signin (UC-24, FR-GO-01/FR-GO-02): the main flow
// in both directions for a System Admin and for an existing owner, AF-24a (scope unknown or
// logically deleted), AF-24b (a Scope Admin who owns a different scope), AF-24c (a body that omits
// `enabled`), and the framework flows the actor list produces (403 for a User, 401
// unauthenticated). Every refusal asserts the persisted flag did not move — that is the whole point
// of AF-24b.
[Collection(nameof(FunctionalCollection))]
public class ScopeControllerSetGoogleSignInTests(PostgresFixture db) : WebApiTest<Program>(EnvironmentType.Local)
{
    private static string Route(Guid scopeId) => $"/api/scopes/{scopeId}/google-signin";

    /// <summary>
    ///     Sends the body SRD §5.1 specifies — <c>{ enabled: bool }</c> — as an anonymous object
    ///     rather than a serialized command, so the test pins the wire contract a client actually
    ///     uses instead of the command's own shape.
    /// </summary>
    private Task<HttpOutput<DataOutput<SetGoogleSignInCommandOutput?>?>> SetAsync(Guid scopeId, bool enabled) =>
        Gateway.PutAsync<DataOutput<SetGoogleSignInCommandOutput?>>(Route(scopeId), new { enabled });

    private async Task<Scope> SeedScopeAsync(bool googleSignInEnabled = false, bool isDeleted = false)
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

    private async Task<Person> SeedScopeAdminAsync(Scope? ownedScope = null)
    {
        await using var context = db.CreateContext();
        var person = new Person
        {
            PublicId = Guid.NewGuid(), Name = "Admin",
            Email = $"admin-{Guid.NewGuid():N}@test.local",
            RoleId = (long)Roles.ScopeAdmin, EmailVerified = true
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

    private async Task<Person> SeedUserAsync(Scope scope)
    {
        await using var context = db.CreateContext();
        var person = new Person
        {
            PublicId = Guid.NewGuid(), Name = "User",
            Email = $"user-{Guid.NewGuid():N}@test.local",
            RoleId = (long)Roles.User, EmailVerified = true
        };
        context.Persons.Add(person);
        await context.SaveChangesAsync();

        context.ScopeUsers.Add(new ScopeUser { ScopeId = scope.Id, PersonId = person.Id });
        // Person.ScopeId mirrors the membership row, as the application writes it — without
        // it the seeded User sits outside the per-scope uniqueness index.
        person.ScopeId = scope.Id;
        await context.SaveChangesAsync();

        return person;
    }

    private async Task<bool> PersistedFlagAsync(Scope scope)
    {
        await using var context = db.CreateContext();
        var persisted = await context.Scopes.AsNoTracking().FirstAsync(x => x.PublicId == scope.PublicId);
        return persisted.GoogleSignInEnabled;
    }

    [FunctionalFact]
    public async Task GivenSystemAdmin_WhenPutGoogleSignInEnabled_ThenOkAndFlagIsEnabled()
    {
        // Given a scope with Google Sign-In off (UC-24 main flow)
        var scope = await SeedScopeAsync();
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await SetAsync(scope.PublicId, enabled: true);

        // Then — response
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(scope.PublicId, response.Body?.Data?.Id);
        Assert.True(response.Body?.Data?.GoogleSignInEnabled);
        Assert.Contains(ScopeMessages.GoogleSignInUpdatedSuccessfully, response.Body!.Messages);

        // Then — database state
        Assert.True(await PersistedFlagAsync(scope));
    }

    [FunctionalFact]
    public async Task GivenSystemAdminAndEnabledScope_WhenPutGoogleSignInDisabled_ThenOkAndFlagIsDisabled()
    {
        // Given a scope already enabled — the "Disable" half of UC-24
        var scope = await SeedScopeAsync(googleSignInEnabled: true);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await SetAsync(scope.PublicId, enabled: false);

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Body?.Data?.GoogleSignInEnabled);
        Assert.False(await PersistedFlagAsync(scope));
    }

    [FunctionalFact]
    public async Task GivenExistingOwner_WhenPutGoogleSignIn_ThenOkAndFlagIsEnabled()
    {
        // Given a Scope Admin who owns the scope (FR-GO-02: owners may toggle their own scope)
        var scope = await SeedScopeAsync();
        var actor = await SeedScopeAdminAsync(ownedScope: scope);
        Authorize(TestTokens.For(actor.PublicId, (int)Roles.ScopeAdmin));

        // When
        var response = await SetAsync(scope.PublicId, enabled: true);

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Body?.Data?.GoogleSignInEnabled);
        Assert.Equal([actor.PublicId], response.Body?.Data?.OwnerIds);
        Assert.True(await PersistedFlagAsync(scope));
    }

    [FunctionalFact]
    public async Task GivenScopeAdminOfAnotherScope_WhenPutGoogleSignIn_ThenForbidden()
    {
        // Given a Scope Admin who owns some other scope (AF-24b)
        var scope = await SeedScopeAsync();
        var otherScope = await SeedScopeAsync();
        var actor = await SeedScopeAdminAsync(ownedScope: otherScope);
        Authorize(TestTokens.For(actor.PublicId, (int)Roles.ScopeAdmin));

        // When
        var response = await SetAsync(scope.PublicId, enabled: true);

        // Then
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains(ScopeMessages.NotScopeOwner, response.Body!.Errors);
        Assert.False(await PersistedFlagAsync(scope));
    }

    [FunctionalFact]
    public async Task GivenUserRole_WhenPutGoogleSignIn_ThenForbidden()
    {
        // Given a caller holding the User role — refused by the attribute, before any handler runs
        var scope = await SeedScopeAsync();
        var user = await SeedUserAsync(scope);
        Authorize(TestTokens.For(user.PublicId, (int)Roles.User));

        // When
        var response = await SetAsync(scope.PublicId, enabled: true);

        // Then
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(await PersistedFlagAsync(scope));
    }

    [FunctionalFact]
    public async Task GivenUnknownScope_WhenPutGoogleSignIn_ThenNotFound()
    {
        // AF-24a
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        var response = await SetAsync(Guid.NewGuid(), enabled: true);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains(ScopeMessages.ScopeNotFound, response.Body!.Errors);
    }

    [FunctionalFact]
    public async Task GivenLogicallyDeletedScope_WhenPutGoogleSignIn_ThenNotFound()
    {
        // AF-24a treats a logically deleted scope as absent — FR-GO-13 would refuse Google sign-in
        // for it anyway, so the flag could never take effect
        var scope = await SeedScopeAsync(isDeleted: true);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        var response = await SetAsync(scope.PublicId, enabled: true);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.False(await PersistedFlagAsync(scope));
    }

    [FunctionalFact]
    public async Task GivenEmptyBody_WhenPutGoogleSignIn_ThenBadRequestAndFlagIsUnchanged()
    {
        // Given an enabled scope and a request that never said which value to set (AF-24c). Without
        // the nullable Enabled and its validator this would bind to false and disable the scope.
        var scope = await SeedScopeAsync(googleSignInEnabled: true);
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await Gateway.PutAsync<DataOutput<SetGoogleSignInCommandOutput?>>(
            Route(scope.PublicId), new { });

        // Then
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(ScopeMessages.EnabledRequired, response.Body!.Errors);
        Assert.True(await PersistedFlagAsync(scope));
    }

    [FunctionalFact]
    public async Task GivenNoToken_WhenPutGoogleSignIn_ThenUnauthorized()
    {
        var scope = await SeedScopeAsync();

        var response = await SetAsync(scope.PublicId, enabled: true);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.False(await PersistedFlagAsync(scope));
    }
}
