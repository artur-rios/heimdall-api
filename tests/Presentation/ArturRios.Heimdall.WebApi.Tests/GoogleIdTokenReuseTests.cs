using System.Net;
using ArturRios.Configuration.Enums;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Data.Persistence;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Heimdall.WebApi.Tests.Support;
using ArturRios.Output;
using ArturRios.Util.Http;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Functional;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Heimdall.WebApi.Tests;

// Functional tests for FR-GO-21 (Threat Model TH-19): a Google ID token is accepted once. A second
// presentation — to sign in again, to sign up in another scope, or to prove presence for an erasure
// request — is answered as an invalid token is, and creates nothing; so is the loser of two
// simultaneous presentations. The host verifies tokens with LocalGoogleIdTokenVerifier (see
// PostgresFixture), whose tokens carry a jti as Google's do unless a test asks for none.
[Collection(nameof(FunctionalCollection))]
public class GoogleIdTokenReuseTests(PostgresFixture db) : WebApiTest<Program>(EnvironmentType.Local)
{
    private Task<HttpOutput<DataOutput<GoogleSignInCommandOutput?>?>> SignInAsync(Guid scopeId, string idToken) =>
        Gateway.PostAsync<DataOutput<GoogleSignInCommandOutput?>>(
            "/api/auth/google", new GoogleSignInCommand { ScopeId = scopeId, IdToken = idToken });

    private async Task<List<GoogleUser>> StoredAsync(Scope scope)
    {
        await using var context = db.CreateContext();

        return await context.GoogleUsers.Where(x => x.ScopeId == scope.Id).ToListAsync();
    }

    [FunctionalFact]
    public async Task GivenATokenThatSignedSomebodyUp_WhenPresentedAgain_ThenUnauthorizedAndNothingMoreIsCreated()
    {
        // Given
        var scope = await Seeds.ScopeAsync(db, new ScopeOptions { GoogleSignInEnabled = true });
        var token = TestGoogleTokens.For();
        var first = await SignInAsync(scope.PublicId, token);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        // When
        var second = await SignInAsync(scope.PublicId, token);

        // Then — answered exactly as an invalid token (AF-25a), with no token issued
        Assert.Equal(HttpStatusCode.Unauthorized, second.StatusCode);
        Assert.Equal([AuthMessages.GoogleAuthenticationFailed], second.Body!.Errors);
        Assert.Null(second.Body.Data);
        Assert.Single(await StoredAsync(scope));
    }

    [FunctionalFact]
    public async Task GivenAUsedToken_WhenPresentedToAnotherScope_ThenNoAccountIsCreatedThere()
    {
        // Given a token already used to sign up in one scope
        var used = await Seeds.ScopeAsync(db, new ScopeOptions { GoogleSignInEnabled = true });
        var other = await Seeds.ScopeAsync(db, new ScopeOptions { GoogleSignInEnabled = true });
        var token = TestGoogleTokens.For();
        Assert.Equal(HttpStatusCode.OK, (await SignInAsync(used.PublicId, token)).StatusCode);

        // When
        var response = await SignInAsync(other.PublicId, token);

        // Then the refusal comes before anything is created
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(await StoredAsync(other));
    }

    [FunctionalFact]
    public async Task GivenTokensWithoutAnId_WhenEachIsPresented_ThenEachIsAcceptedOnceByItsSignedPart()
    {
        // Given two tokens for one Google account that carry no jti, differing only in their expiry
        var scope = await Seeds.ScopeAsync(db, new ScopeOptions { GoogleSignInEnabled = true });
        var subject = $"google-sub-{Guid.NewGuid():N}";
        var email = $"no-jti-{Guid.NewGuid():N}@gmail.test";
        var first = TestGoogleTokens.For(subject, email, expiresIn: TimeSpan.FromMinutes(10), withTokenId: false);
        var second = TestGoogleTokens.For(subject, email, expiresIn: TimeSpan.FromMinutes(11), withTokenId: false);

        // When
        var firstUse = await SignInAsync(scope.PublicId, first);
        var otherToken = await SignInAsync(scope.PublicId, second);
        var replay = await SignInAsync(scope.PublicId, first);

        // Then
        Assert.Equal(HttpStatusCode.OK, firstUse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, otherToken.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        Assert.Equal([AuthMessages.GoogleAuthenticationFailed], replay.Body!.Errors);

        await using var context = db.CreateContext();
        Assert.True(await context.GoogleIdTokenUses.AnyAsync(x => x.Id == GoogleIdTokenUse.KeyOf(null, first)));
    }

    [FunctionalFact]
    public async Task GivenTheSameTokenPresentedTwiceAtOnce_WhenBothArrive_ThenExactlyOneSucceeds()
    {
        // Given
        var scope = await Seeds.ScopeAsync(db, new ScopeOptions { GoogleSignInEnabled = true });
        var token = TestGoogleTokens.For();

        // When
        var responses = await Task.WhenAll(
            SignInAsync(scope.PublicId, token),
            SignInAsync(scope.PublicId, token));

        // Then
        Assert.Single(responses, x => x.StatusCode == HttpStatusCode.OK);
        Assert.Single(responses, x => x.StatusCode == HttpStatusCode.Unauthorized);
        Assert.Single(await StoredAsync(scope));
    }

    [FunctionalFact]
    public async Task GivenManySimultaneousRecordsOfOneToken_WhenWritten_ThenExactlyOneIsAccepted()
    {
        // Given one key, recorded through as many connections at once
        var key = GoogleIdTokenUse.KeyOf(Guid.NewGuid().ToString("N"), "unused");
        var expiresAt = DateTime.UtcNow.AddMinutes(30);

        // When
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            await using var context = db.CreateContext();
            return await new AtomicWrites(context).TryRecordGoogleIdTokenUseAsync(key, expiresAt);
        }));

        // Then
        Assert.Single(results, recorded => recorded);

        await using var check = db.CreateContext();
        var stored = await check.GoogleIdTokenUses.SingleAsync(x => x.Id == key);
        Assert.Equal(expiresAt, stored.ExpiresAt, TimeSpan.FromMilliseconds(1));
    }

    [FunctionalFact]
    public async Task GivenTheTokenThatSignedAGoogleUserIn_WhenPresentedForTheirErasureRequest_ThenNotAccepted()
    {
        // Given a Google User signed in with a token, and holding the bearer token it bought
        var scope = await Seeds.ScopeAsync(db, new ScopeOptions { GoogleSignInEnabled = true });
        var token = TestGoogleTokens.For();
        var signIn = await SignInAsync(scope.PublicId, token);
        Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);
        Authorize(signIn.Body!.Data!.Token);

        // When the same token is offered as the proof of presence (AF-42a)
        var response = await Gateway.PostAsync<DataOutput<RequestErasureCommandOutput?>>(
            "/api/auth/erasure-request", new { idToken = token });

        // Then
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal([ErasureMessages.CredentialNotAccepted], response.Body!.Errors);
        var googleUser = Assert.Single(await StoredAsync(scope));
        Assert.Null(googleUser.ErasureRequestedAt);
        Assert.False(googleUser.IsDeleted);
    }

    [FunctionalFact]
    public async Task GivenAFreshTokenForTheSameAccount_WhenPresentedForTheirErasureRequest_ThenItIsRecorded()
    {
        // Given
        var scope = await Seeds.ScopeAsync(db, new ScopeOptions { GoogleSignInEnabled = true });
        var subject = $"google-sub-{Guid.NewGuid():N}";
        var email = $"erase-{Guid.NewGuid():N}@gmail.test";
        var signIn = await SignInAsync(scope.PublicId, TestGoogleTokens.For(subject, email));
        Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);
        Authorize(signIn.Body!.Data!.Token);

        // When
        var response = await Gateway.PostAsync<DataOutput<RequestErasureCommandOutput?>>(
            "/api/auth/erasure-request", new { idToken = TestGoogleTokens.For(subject, email) });

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(Assert.Single(await StoredAsync(scope)).ErasureRequestedAt);
    }
}
