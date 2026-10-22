using System.Net;
using ArturRios.Configuration.Enums;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Command.Services;
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

// Functional tests for POST /api/auth/password-recovery (UC-12, FR-PR-01/02). The response is the
// same on every path by design, so the response assertions alone cannot tell the main flow from
// AF-12a — each test also opens the database and asserts whether a password_reset_token row exists.
// That row, and only that row, is the difference between the two.
//
// The row is written after the response, by PasswordRecoveryDispatcher (AF-12a's timing half), so
// every database assertion first waits for the queue to settle — see SettleAsync.
[Collection(nameof(FunctionalCollection))]
public class AuthControllerPasswordRecoveryTests(PostgresFixture db) : WebApiTest<Program>(EnvironmentType.Local)
{
    private const string Password = Seeds.Password;

    private static string UniqueEmail(string prefix) => $"{prefix}-{Guid.NewGuid():N}@test.local";

    private Task<HttpOutput<DataOutput<PasswordRecoveryCommandOutput?>?>> RecoverAsync(
        string email, Guid? scopeId = null) =>
        Gateway.PostAsync<DataOutput<PasswordRecoveryCommandOutput?>>(
            "/api/auth/password-recovery",
            new PasswordRecoveryCommand { Email = email, ScopeId = scopeId });

    /// <summary>
    ///     Waits until every recovery requested so far has been worked through. The dispatcher takes
    ///     requests one at a time, in order, so once a sentinel request made last has produced its
    ///     token, every earlier one has been processed too — including those that rightly produced
    ///     nothing, which a plain "wait for a row" could never confirm.
    /// </summary>
    private async Task SettleAsync()
    {
        var sentinel = await Seeds.PersonAsync(db, Roles.SystemAdmin, new PersonOptions { Email = UniqueEmail("sentinel") });

        await RecoverAsync(sentinel.Email);

        await Poll.UntilAsync(
            async () => (await TokensForAsync(sentinel)).Count > 0,
            "The recovery queue did not settle in time.");
    }

    private async Task<List<PasswordResetToken>> TokensForAsync(Person person)
    {
        await using var context = db.CreateContext();

        return await context.PasswordResetTokens
            .Where(token => token.PersonId == person.Id)
            .ToListAsync();
    }

    /// <summary>
    ///     The response every path produces. Asserted with the same helper everywhere precisely
    ///     because sameness is the requirement: if a future change makes one path answer
    ///     differently, every test using this fails.
    /// </summary>
    private static void AssertGenericSuccess(HttpOutput<DataOutput<PasswordRecoveryCommandOutput?>?> response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(AuthMessages.PasswordRecoveryRequested, response.Body!.Messages);
        Assert.Empty(response.Body.Errors);
    }

    [FunctionalFact]
    public async Task GivenUserWithScopeId_WhenPostPasswordRecovery_ThenTokenIsStoredForThem()
    {
        // Given a User of a live scope
        var scope = await Seeds.ScopeAsync(db);
        var email = UniqueEmail("user");
        var person = await Seeds.PersonAsync(db, Roles.User, new PersonOptions { Email = email, MemberOf = scope });

        // When
        var response = await RecoverAsync(email, scope.PublicId);

        // Then — response
        AssertGenericSuccess(response);
        await SettleAsync();

        // Then — database state: one unused token, expiring in the future (FR-PR-02)
        var token = Assert.Single(await TokensForAsync(person));
        Assert.False(token.Used);
        Assert.Equal(SingleUseTokenHash.Length, token.TokenHash.Length);
        Assert.True(token.ExpiresAt > DateTime.UtcNow);
    }

    [FunctionalFact]
    public async Task GivenScopeAdminWithoutScopeId_WhenPostPasswordRecovery_ThenTokenIsStoredForThem()
    {
        // Given a ScopeAdmin owning a live scope, recovering without naming one
        var scope = await Seeds.ScopeAsync(db);
        var email = UniqueEmail("admin");
        var person = await Seeds.PersonAsync(db, Roles.ScopeAdmin, new PersonOptions { Email = email, Owns = [scope] });

        // When
        var response = await RecoverAsync(email);

        // Then
        AssertGenericSuccess(response);
        await SettleAsync();
        Assert.Single(await TokensForAsync(person));
    }

    [FunctionalFact]
    public async Task GivenEmailDifferingInCase_WhenPostPasswordRecovery_ThenTokenIsStoredForThem()
    {
        // Given a stored email in mixed case
        var email = UniqueEmail("MiXeD").ToUpper();
        var person = await Seeds.PersonAsync(db, Roles.SystemAdmin, new PersonOptions { Email = email });

        // When recovering with the lower-case form
        var response = await RecoverAsync(email.ToLower());

        // Then
        AssertGenericSuccess(response);
        await SettleAsync();
        Assert.Single(await TokensForAsync(person));
    }

    [FunctionalFact]
    public async Task GivenTwoRequestsInsideTheCooldown_WhenPostPasswordRecovery_ThenOneTokenIsStoredAndBothAreAnsweredAlike()
    {
        // Given someone who asks twice — a lost first email, an impatient second click — or someone
        // else asking again and again for the same address
        var email = UniqueEmail("admin");
        var person = await Seeds.PersonAsync(db, Roles.SystemAdmin, new PersonOptions { Email = email });

        // When
        var first = await RecoverAsync(email);
        var second = await RecoverAsync(email);

        // Then — AF-12b (FR-PR-07): one link per person per cooldown. Both are answered as any request
        // is, and the first link, which outlives the cooldown, is the one that works
        AssertGenericSuccess(first);
        AssertGenericSuccess(second);
        await SettleAsync();
        var token = Assert.Single(await TokensForAsync(person));
        Assert.False(token.Used);
    }

    [FunctionalFact]
    public async Task GivenUnknownEmail_WhenPostPasswordRecovery_ThenAnswerIsUnchangedAndNoTokenIsStored()
    {
        // Given — AF-12a: the address belongs to nobody
        // When
        var response = await RecoverAsync(UniqueEmail("nobody"));

        // Then — indistinguishable from the main flow above
        AssertGenericSuccess(response);

        // Then — database state: nothing was issued at all
        await SettleAsync();
        await using var context = db.CreateContext();
        Assert.Equal(0, await context.PasswordResetTokens.CountAsync(
            token => token.Person.Email.StartsWith("nobody-")));
    }

    [FunctionalFact]
    public async Task GivenUserAndWrongScopeId_WhenPostPasswordRecovery_ThenNoTokenIsStored()
    {
        // Given — AF-12a: the User exists, but in another scope
        var theirScope = await Seeds.ScopeAsync(db);
        var otherScope = await Seeds.ScopeAsync(db);
        var email = UniqueEmail("user");
        var person = await Seeds.PersonAsync(db, Roles.User, new PersonOptions { Email = email, MemberOf = theirScope });

        // When
        var response = await RecoverAsync(email, otherScope.PublicId);

        // Then
        AssertGenericSuccess(response);
        await SettleAsync();
        Assert.Empty(await TokensForAsync(person));
    }

    [FunctionalFact]
    public async Task GivenUserEmailWithoutScopeId_WhenPostPasswordRecovery_ThenNoTokenIsStored()
    {
        // Given — AF-12a: without a scope id the admin lookup runs, which must not reach a User
        var scope = await Seeds.ScopeAsync(db);
        var email = UniqueEmail("user");
        var person = await Seeds.PersonAsync(db, Roles.User, new PersonOptions { Email = email, MemberOf = scope });

        // When
        var response = await RecoverAsync(email);

        // Then
        AssertGenericSuccess(response);
        await SettleAsync();
        Assert.Empty(await TokensForAsync(person));
    }

    [FunctionalFact]
    public async Task GivenLogicallyDeletedPerson_WhenPostPasswordRecovery_ThenNoTokenIsStored()
    {
        // Given an account UC-11 refuses to authenticate: a reset link would produce a password that
        // cannot be used, and answering differently would confirm the account exists
        var email = UniqueEmail("deleted");
        var person = await Seeds.PersonAsync(db, Roles.SystemAdmin, new PersonOptions { Email = email, IsDeleted = true });

        // When
        var response = await RecoverAsync(email);

        // Then
        AssertGenericSuccess(response);
        await SettleAsync();
        Assert.Empty(await TokensForAsync(person));
    }

    [FunctionalFact]
    public async Task GivenUserWhoseScopeIsDeleted_WhenPostPasswordRecovery_ThenNoTokenIsStored()
    {
        // Given a live User of a deleted scope — refused at login by AF-11d
        var scope = await Seeds.ScopeAsync(db, new ScopeOptions { IsDeleted = true });
        var email = UniqueEmail("user");
        var person = await Seeds.PersonAsync(db, Roles.User, new PersonOptions { Email = email, MemberOf = scope });

        // When
        var response = await RecoverAsync(email, scope.PublicId);

        // Then
        AssertGenericSuccess(response);
        await SettleAsync();
        Assert.Empty(await TokensForAsync(person));
    }

    [FunctionalFact]
    public async Task GivenScopeAdminWhoseScopesAreAllDeleted_WhenPostPasswordRecovery_ThenNoTokenIsStored()
    {
        // Given a ScopeAdmin with nothing left to administer — refused at login by AF-11e
        var email = UniqueEmail("admin");
        var person = await Seeds.PersonAsync(db, Roles.ScopeAdmin, new PersonOptions { Email = email, Owns = [await Seeds.ScopeAsync(db, new ScopeOptions { IsDeleted = true }), await Seeds.ScopeAsync(db, new ScopeOptions { IsDeleted = true })] });

        // When
        var response = await RecoverAsync(email);

        // Then
        AssertGenericSuccess(response);
        await SettleAsync();
        Assert.Empty(await TokensForAsync(person));
    }

    [FunctionalFact]
    public async Task GivenRegisteredAndUnknownEmail_WhenPostPasswordRecovery_ThenResponsesAreIdentical()
    {
        // Given one address that exists and one that does not. Each preceding test asserts its own
        // path; this one puts the two responses side by side, which is the property AF-12a actually
        // states — a caller comparing them must find nothing to tell them apart.
        var email = UniqueEmail("admin");
        await Seeds.PersonAsync(db, Roles.SystemAdmin, new PersonOptions { Email = email });

        // When
        var known = await RecoverAsync(email);
        var unknown = await RecoverAsync(UniqueEmail("nobody"));

        // Then
        Assert.Equal(known.StatusCode, unknown.StatusCode);
        Assert.Equal(known.Body!.Messages, unknown.Body!.Messages);
        Assert.Equal(known.Body.Errors, unknown.Body.Errors);
        Assert.Equal(known.Body.Data is null, unknown.Body.Data is null);
    }

    [FunctionalTheory]
    [InlineData("")]
    [InlineData("not-an-email")]
    public async Task GivenMalformedEmail_WhenPostPasswordRecovery_ThenBadRequest(string email)
    {
        // Given a request that fails shape validation (NFR-10) — the one rejection this endpoint
        // issues, and one that says nothing about who is registered
        // When
        var response = await RecoverAsync(email);

        // Then
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.NotEmpty(response.Body!.Errors);
    }

    [FunctionalFact]
    public async Task GivenNoBearerToken_WhenPostPasswordRecovery_ThenEndpointAnswersAnonymously()
    {
        // Given no bearer token on the gateway: someone who has lost their password cannot hold one,
        // so the authentication middleware must let the request through ([AllowAnonymous])
        var response = await RecoverAsync(UniqueEmail("anonymous"));

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenAnAddressFreedByADeletedAdmin_WhenItsNewHolderRecovers_ThenTheTokenIsTheirs()
    {
        // A deleted admin awaiting anonymisation still holds the address its live successor now
        // holds (FR-PE-09 is unique among live persons only). The recovery goes to the live one.
        var email = UniqueEmail("reused-admin");
        var deleted = await Seeds.PersonAsync(db, Roles.SystemAdmin, new PersonOptions { Email = email, IsDeleted = true });
        var current = await Seeds.PersonAsync(db, Roles.SystemAdmin, new PersonOptions { Email = email });

        // When
        var response = await RecoverAsync(email);

        // Then
        AssertGenericSuccess(response);
        await SettleAsync();
        Assert.Single(await TokensForAsync(current));
        Assert.Empty(await TokensForAsync(deleted));
    }
}
