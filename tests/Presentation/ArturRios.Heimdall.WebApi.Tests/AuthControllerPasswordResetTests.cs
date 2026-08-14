using System.Net;
using ArturRios.Configuration.Enums;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Heimdall.WebApi.Tests.Support;
using ArturRios.Output;
using ArturRios.Util.Hashing;
using ArturRios.Util.Http;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Functional;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Heimdall.WebApi.Tests;

// Functional tests for POST /api/auth/password-reset (UC-13, FR-PR-03/04): the main flow for a User
// and for an admin, AF-13a…AF-13c (400, each named), AF-13d (400), and the anonymous access the
// endpoint depends on.
//
// The proof that the password really changed is a login: the response says only that it did, and the
// stored hash is meaningless on its own, so the tests reset a password and then authenticate with
// it — and confirm the old one is refused. That also pins the two use cases together end to end.
[Collection(nameof(FunctionalCollection))]
public class AuthControllerPasswordResetTests(PostgresFixture db) : WebApiTest<Program>(EnvironmentType.Local)
{
    private const string OldPassword = "0ld-Reset-Pass!";
    private const string NewPassword = "Str0ng-Reset-Pass!";

    private static string UniqueEmail(string prefix) => $"{prefix}-{Guid.NewGuid():N}@test.local";

    private static string UniqueToken() => $"token-{Guid.NewGuid():N}";

    private async Task<Scope> SeedScopeAsync()
    {
        await using var context = db.CreateContext();
        var scope = new Scope { PublicId = Guid.NewGuid(), Name = $"scope-{Guid.NewGuid():N}" };
        context.Scopes.Add(scope);
        await context.SaveChangesAsync();
        return scope;
    }

    private async Task<Person> SeedPersonAsync(Roles role, string email, bool isDeleted = false)
    {
        await using var context = db.CreateContext();
        var person = new Person
        {
            PublicId = Guid.NewGuid(),
            Name = $"{role}",
            Email = email,
            PasswordHash = Hash.EncodeWithRandomSalt(OldPassword, out var salt),
            Salt = salt,
            RoleId = (long)role,
            EmailVerified = true,
            IsDeleted = isDeleted
        };
        context.Persons.Add(person);
        await context.SaveChangesAsync();
        return person;
    }

    private async Task<Person> SeedUserAsync(Scope scope, string email)
    {
        var person = await SeedPersonAsync(Roles.User, email);

        await using var context = db.CreateContext();
        context.ScopeUsers.Add(new ScopeUser { ScopeId = scope.Id, PersonId = person.Id });
        // Person.ScopeId mirrors the membership row, as the application writes it — without
        // it the seeded User sits outside the per-scope uniqueness index.
        person.ScopeId = scope.Id;
        await context.SaveChangesAsync();

        return person;
    }

    /// <summary>
    ///     Writes a reset token directly rather than going through UC-12's endpoint: these tests are
    ///     about consuming a token, and only a direct write can produce one that is already expired
    ///     or already used.
    /// </summary>
    private async Task<PasswordResetToken> SeedTokenAsync(
        Person person, string? value = null, DateTime? expiresAt = null, bool used = false)
    {
        await using var context = db.CreateContext();
        var token = new PasswordResetToken
        {
            PersonId = person.Id,
            Token = value ?? UniqueToken(),
            ExpiresAt = expiresAt ?? DateTime.UtcNow.AddHours(1),
            Used = used
        };
        context.PasswordResetTokens.Add(token);
        await context.SaveChangesAsync();
        return token;
    }

    private Task<HttpOutput<DataOutput<ResetPasswordCommandOutput?>?>> ResetAsync(
        string token, string password = NewPassword) =>
        Gateway.PostAsync<DataOutput<ResetPasswordCommandOutput?>>(
            "/api/auth/password-reset",
            new ResetPasswordCommand { Token = token, NewPassword = password });

    private Task<HttpOutput<DataOutput<LoginCommandOutput?>?>> LoginAsync(
        string email, string password, Guid? scopeId = null) =>
        Gateway.PostAsync<DataOutput<LoginCommandOutput?>>(
            "/api/auth/login",
            new LoginCommand { Email = email, Password = password, ScopeId = scopeId });

    private async Task<List<PasswordResetToken>> TokensForAsync(Person person)
    {
        await using var context = db.CreateContext();

        return await context.PasswordResetTokens
            .Where(token => token.PersonId == person.Id)
            .ToListAsync();
    }

    [FunctionalFact]
    public async Task GivenLiveToken_WhenPostPasswordReset_ThenPasswordIsChangedAndTokenConsumed()
    {
        // Given an admin holding a reset token
        var email = UniqueEmail("admin");
        var person = await SeedPersonAsync(Roles.SystemAdmin, email);
        var token = await SeedTokenAsync(person);

        // When
        var response = await ResetAsync(token.Token);

        // Then — response
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(AuthMessages.PasswordResetSuccessful, response.Body!.Messages);
        Assert.Empty(response.Body.Errors);

        // Then — database state: the token is spent (UC-13 step 4)
        Assert.True(Assert.Single(await TokensForAsync(person)).Used);

        // Then — the password really changed: the new one authenticates, the old one does not
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(email, NewPassword)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(email, OldPassword)).StatusCode);
    }

    [FunctionalFact]
    public async Task GivenUserOfAScope_WhenPostPasswordReset_ThenPasswordIsChanged()
    {
        // Given a User, whose login needs a scope id but whose reset does not — the token identifies
        // them on its own, which is the whole reason UC-12 made it long and random
        var scope = await SeedScopeAsync();
        var email = UniqueEmail("user");
        var person = await SeedUserAsync(scope, email);
        var token = await SeedTokenAsync(person);

        // When
        var response = await ResetAsync(token.Token);

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            HttpStatusCode.OK,
            (await LoginAsync(email, NewPassword, scope.PublicId)).StatusCode);
    }

    [FunctionalFact]
    public async Task GivenSaltIsRegenerated_WhenPostPasswordReset_ThenStoredSaltAndHashBothChange()
    {
        // Given — UC-13 step 3 asks for a new random salt, not a re-use of the stored one, so the new
        // hash shares nothing with the one it replaces (FR-RO-04, NFR-02)
        var person = await SeedPersonAsync(Roles.SystemAdmin, UniqueEmail("admin"));
        var originalHash = person.PasswordHash;
        var originalSalt = person.Salt;
        var token = await SeedTokenAsync(person);

        // When
        await ResetAsync(token.Token);

        // Then
        await using var context = db.CreateContext();
        var stored = await context.Persons.SingleAsync(x => x.Id == person.Id);
        Assert.NotEqual(originalSalt, stored.Salt);
        Assert.NotEqual(originalHash, stored.PasswordHash);
        Assert.True(stored.UpdatedAt >= stored.CreatedAt);
    }

    [FunctionalFact]
    public async Task GivenTwoLiveTokens_WhenPostPasswordReset_ThenBothAreConsumed()
    {
        // Given someone who clicked "forgot password" twice, so UC-12 issued two working links.
        // UC-12's own tests leave both live and say UC-13 decides; this is that decision.
        var email = UniqueEmail("admin");
        var person = await SeedPersonAsync(Roles.SystemAdmin, email);
        var first = await SeedTokenAsync(person);
        var second = await SeedTokenAsync(person);

        // When the second link is the one clicked
        var response = await ResetAsync(second.Token);

        // Then — both rows are spent
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.All(await TokensForAsync(person), token => Assert.True(token.Used));

        // Then — and the first link cannot change the password a second time (AF-13b)
        var replay = await ResetAsync(first.Token, "An0ther-Pass!");
        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
        Assert.Contains(AuthMessages.TokenAlreadyUsed, replay.Body!.Errors);
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(email, NewPassword)).StatusCode);
    }

    [FunctionalFact]
    public async Task GivenAnotherPersonHoldsALiveToken_WhenPostPasswordReset_ThenTheirTokenSurvives()
    {
        // Given the boundary of the rule above: invalidation reaches the person's own tokens and stops
        var person = await SeedPersonAsync(Roles.SystemAdmin, UniqueEmail("admin"));
        var other = await SeedPersonAsync(Roles.SystemAdmin, UniqueEmail("other"));
        var theirToken = await SeedTokenAsync(other);

        // When
        await ResetAsync((await SeedTokenAsync(person)).Token);

        // Then
        Assert.False(Assert.Single(await TokensForAsync(other)).Used);
        Assert.Equal(theirToken.Token, (await TokensForAsync(other)).Single().Token);
    }

    [FunctionalFact]
    public async Task GivenExpiredToken_WhenPostPasswordReset_ThenBadRequestAndPasswordIsUnchanged()
    {
        // Given — AF-13a (FR-PR-04)
        var email = UniqueEmail("admin");
        var person = await SeedPersonAsync(Roles.SystemAdmin, email);
        var token = await SeedTokenAsync(person, expiresAt: DateTime.UtcNow.AddHours(-1));

        // When
        var response = await ResetAsync(token.Token);

        // Then — response
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(AuthMessages.TokenExpired, response.Body!.Errors);

        // Then — nothing changed: the old password still works and the token is still unused
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(email, OldPassword)).StatusCode);
        Assert.False(Assert.Single(await TokensForAsync(person)).Used);
    }

    [FunctionalFact]
    public async Task GivenUsedToken_WhenPostPasswordReset_ThenBadRequestAndPasswordIsUnchanged()
    {
        // Given — AF-13b
        var email = UniqueEmail("admin");
        var person = await SeedPersonAsync(Roles.SystemAdmin, email);
        var token = await SeedTokenAsync(person, used: true);

        // When
        var response = await ResetAsync(token.Token);

        // Then
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(AuthMessages.TokenAlreadyUsed, response.Body!.Errors);
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(email, OldPassword)).StatusCode);
    }

    [FunctionalFact]
    public async Task GivenUnknownToken_WhenPostPasswordReset_ThenBadRequest()
    {
        // Given — AF-13c: nothing matches what the caller presented
        // When
        var response = await ResetAsync(UniqueToken());

        // Then
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(AuthMessages.TokenInvalid, response.Body!.Errors);
    }

    [FunctionalFact]
    public async Task GivenTokenDifferingOnlyInCase_WhenPostPasswordReset_ThenBadRequest()
    {
        // Given — AF-13c. The lookup is case-sensitive, unlike every email comparison in this system:
        // the token is a random secret, and folding its case would throw away part of its alphabet.
        var email = UniqueEmail("admin");
        var person = await SeedPersonAsync(Roles.SystemAdmin, email);
        var token = await SeedTokenAsync(person, $"MiXeD-{Guid.NewGuid():N}".ToUpper());

        // When
        var response = await ResetAsync(token.Token.ToLower());

        // Then
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(AuthMessages.TokenInvalid, response.Body!.Errors);
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(email, OldPassword)).StatusCode);
    }

    [FunctionalTheory]
    [InlineData("", NewPassword)]
    [InlineData("some-token", "")]
    [InlineData("some-token", "short")]
    public async Task GivenMalformedRequest_WhenPostPasswordReset_ThenBadRequest(string token, string password)
    {
        // Given — AF-13d: a missing token, a missing password, and one below UC-06's eight-character
        // floor, which a reset must not be a way around
        // When
        var response = await ResetAsync(token, password);

        // Then
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.NotEmpty(response.Body!.Errors);
    }

    [FunctionalFact]
    public async Task GivenLogicallyDeletedPerson_WhenPostPasswordReset_ThenPasswordIsChangedButLoginStillFails()
    {
        // Given a deletion that landed between the email and the click. UC-13 defines no alternative
        // flow for it, and the reset grants nothing: UC-11 refuses the login by AF-11c either way.
        var email = UniqueEmail("deleted");
        var person = await SeedPersonAsync(Roles.SystemAdmin, email, isDeleted: true);
        var token = await SeedTokenAsync(person);

        // When
        var response = await ResetAsync(token.Token);

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(Assert.Single(await TokensForAsync(person)).Used);
        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(email, NewPassword)).StatusCode);
    }

    [FunctionalFact]
    public async Task GivenNoBearerToken_WhenPostPasswordReset_ThenEndpointAnswersAnonymously()
    {
        // Given no bearer token on the gateway: someone resetting a password they have lost cannot
        // hold one, so the authentication middleware must let the request through ([AllowAnonymous]).
        // An unknown reset token reaches the handler and is refused there, on its merits — a 401 here
        // would mean the endpoint never ran at all.
        var response = await ResetAsync(UniqueToken());

        // Then
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(AuthMessages.TokenInvalid, response.Body!.Errors);
    }

    [FunctionalFact]
    public async Task GivenRecoveryRequested_WhenResettingWithTheIssuedToken_ThenPasswordIsChanged()
    {
        // Given the two use cases joined as a person actually meets them: UC-12 issues the token,
        // UC-13 spends it. Nothing here reaches into the database to build a token by hand.
        var email = UniqueEmail("admin");
        await SeedPersonAsync(Roles.SystemAdmin, email);

        var recovery = await Gateway.PostAsync<DataOutput<PasswordRecoveryCommandOutput?>>(
            "/api/auth/password-recovery", new PasswordRecoveryCommand { Email = email });

        Assert.Equal(HttpStatusCode.OK, recovery.StatusCode);

        // The token itself never appears in a response — it is mailed. The functional suite has no
        // Mailgun credentials, so it is read back from the row UC-12 wrote.
        await using var context = db.CreateContext();
        var issued = await context.PasswordResetTokens
            .Include(token => token.Person)
            .SingleAsync(token => token.Person.Email == email);

        // When
        var response = await ResetAsync(issued.Token);

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(email, NewPassword)).StatusCode);
    }
}
