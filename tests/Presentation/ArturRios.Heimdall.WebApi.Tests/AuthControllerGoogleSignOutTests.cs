using System.Net;
using ArturRios.Configuration.Enums;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Heimdall.WebApi.Security;
using ArturRios.Heimdall.WebApi.Tests.Support;
using ArturRios.Output;
using ArturRios.Util.Http;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Functional;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Heimdall.WebApi.Tests;

// Functional tests for POST /api/auth/google/sign-out (UC-26, FR-GO-18): the main flow, including a
// round trip that signs in through UC-25 and signs out with the token that call returned, and every
// shape of AF-26a — no token at all, a token naming a Google User UC-28 logically deleted, a token
// naming nobody (the Google User was hard deleted, or the caller is a password User), and an
// administrator's token, since FR-GO-04 means neither administrator role can ever be a Google User.
//
// One test asserts the row survives the sign-out: under the stateless token strategy the endpoint
// writes nothing, and the difference between signing out and being deleted must stay visible.
[Collection(nameof(FunctionalCollection))]
public class AuthControllerGoogleSignOutTests(PostgresFixture db) : WebApiTest<Program>(EnvironmentType.Local)
{
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

    private async Task<GoogleUser> SeedGoogleUserAsync(Scope scope, bool isDeleted = false)
    {
        await using var context = db.CreateContext();

        var googleUser = new GoogleUser
        {
            PublicId = Guid.NewGuid(),
            GoogleId = $"google-sub-{Guid.NewGuid():N}",
            Name = "Signed-In Google User",
            Email = $"signer-{Guid.NewGuid():N}@gmail.test",
            EmailVerified = true,
            ScopeId = scope.Id,
            IsDeleted = isDeleted
        };

        context.GoogleUsers.Add(googleUser);
        await context.SaveChangesAsync();

        return googleUser;
    }

    /// <summary>
    ///     The request carries no body — the Google User comes from the bearer token — but
    ///     <c>HttpGateway.PostAsync</c> takes a payload, so an empty object is sent and the action,
    ///     which binds no body, ignores it. The same arrangement
    ///     <see cref="AuthControllerResendVerificationTests" /> uses.
    /// </summary>
    private Task<HttpOutput<DataOutput<GoogleSignOutCommandOutput?>?>> SignOutAsync() =>
        Gateway.PostAsync<DataOutput<GoogleSignOutCommandOutput?>>("/api/auth/google/sign-out", new { });

    private async Task<GoogleUser?> StoredAsync(Guid publicId)
    {
        await using var context = db.CreateContext();

        return await context.GoogleUsers.FirstOrDefaultAsync(x => x.PublicId == publicId);
    }

    [FunctionalFact]
    public async Task GivenLiveGoogleUser_WhenPostGoogleSignOut_ThenSucceeds()
    {
        // Given a Google User holding a token UC-25 issued them (UC-26 main flow)
        var scope = await SeedScopeAsync();
        var googleUser = await SeedGoogleUserAsync(scope);
        Authorize(TestTokens.For(googleUser.PublicId, (int)Roles.User, scope.PublicId));

        // When
        var response = await SignOutAsync();

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(AuthMessages.GoogleSignOutSuccessful, response.Body!.Messages);
        Assert.Empty(response.Body.Errors);
    }

    [FunctionalFact]
    public async Task GivenGoogleSignIn_WhenPostGoogleSignOutWithTheIssuedToken_ThenSucceeds()
    {
        // Given a caller who signed in through UC-25 and holds exactly what that call returned —
        // UC-26's precondition, proved end to end rather than assumed
        var scope = await SeedScopeAsync();
        var signIn = await Gateway.PostAsync<DataOutput<GoogleSignInCommandOutput?>>(
            "/api/auth/google",
            new { ScopeId = scope.PublicId, IdToken = TestGoogleTokens.For() });

        Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);
        Authorize(signIn.Body!.Data!.Token);

        // When
        var response = await SignOutAsync();

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(AuthMessages.GoogleSignOutSuccessful, response.Body!.Messages);
    }

    [FunctionalFact]
    public async Task GivenSignedOutGoogleUser_WhenReadingTheRowBack_ThenItIsUnchanged()
    {
        // Given a Google User who signs out (UC-26 step 2: nothing is revoked and nothing is
        // written — signing out is not a deletion)
        var scope = await SeedScopeAsync();
        var googleUser = await SeedGoogleUserAsync(scope);
        Authorize(TestTokens.For(googleUser.PublicId, (int)Roles.User, scope.PublicId));

        // When
        await SignOutAsync();

        // Then
        var stored = await StoredAsync(googleUser.PublicId);
        Assert.NotNull(stored);
        Assert.False(stored.IsDeleted);
        Assert.Equal(googleUser.GoogleId, stored.GoogleId);
    }

    [FunctionalFact]
    public async Task GivenAnonymousCaller_WhenPostGoogleSignOut_ThenUnauthorized()
    {
        // Given no bearer token at all — the half of AF-26a authentication answers before the
        // handler is ever reached
        var response = await SignOutAsync();

        // Then
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenLogicallyDeletedGoogleUser_WhenPostGoogleSignOut_ThenUnauthorized()
    {
        // Given a token that outlived UC-28's logical deletion — reachable because authentication
        // runs in ClaimsOnly mode (AF-26a)
        var scope = await SeedScopeAsync();
        var googleUser = await SeedGoogleUserAsync(scope, isDeleted: true);
        Authorize(TestTokens.For(googleUser.PublicId, (int)Roles.User, scope.PublicId));

        // When
        var response = await SignOutAsync();

        // Then — ActorLivenessFilter answers first: a token naming a deleted identity is refused
        // for every endpoint, not just this one, so the refusal arrives before UC-26's own AF-26a
        // check ever runs. The status is the 401 AF-26a specifies either way.
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(ActorLivenessFilter.ActorNotLive, response.Body!.Errors);
    }

    [FunctionalFact]
    public async Task GivenTokenNamingNoGoogleUser_WhenPostGoogleSignOut_ThenUnauthorized()
    {
        // Given a User-role token whose id belongs to no Google User — a password User signed in
        // through UC-11, or a Google User UC-29 hard deleted (AF-26a)
        var scope = await SeedScopeAsync();
        Authorize(TestTokens.For(Guid.NewGuid(), (int)Roles.User, scope.PublicId));

        // When
        var response = await SignOutAsync();

        // Then — the same answer the deleted-account flow gives, so neither reveals whether the
        // account exists. Both are now ActorLivenessFilter's, which is where "this token names
        // nobody" is decided for the whole API.
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(ActorLivenessFilter.ActorNotLive, response.Body!.Errors);
    }

    [FunctionalTheory]
    [InlineData(Roles.SystemAdmin)]
    [InlineData(Roles.ScopeAdmin)]
    public async Task GivenAdministrator_WhenPostGoogleSignOut_ThenUnauthorized(Roles role)
    {
        // Given an administrator, who can never be a Google User (FR-GO-04) and is marked
        // not-applicable for this operation in the authorization matrix (AF-26a)
        Authorize(TestTokens.ForRole((int)role));

        // When
        var response = await SignOutAsync();

        // Then
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(AuthMessages.GoogleAuthenticationFailed, response.Body!.Errors);
    }
}
