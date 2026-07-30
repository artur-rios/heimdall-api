using System.Net;
using ArturRios.Configuration.Enums;
using ArturRios.IdentityManager.Command.Input;
using ArturRios.IdentityManager.Command.Output;
using ArturRios.IdentityManager.Domain.Entities;
using ArturRios.IdentityManager.Domain.Enums;
using ArturRios.IdentityManager.Shared.Messages;
using ArturRios.IdentityManager.WebApi.Tests.Support;
using ArturRios.Output;
using ArturRios.Util.Hashing;
using ArturRios.Util.Http;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Functional;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.IdentityManager.WebApi.Tests;

// Functional tests for POST /api/auth/verify-email (UC-14, FR-EV-03): the main flow for a User and
// for an admin, AF-14a…AF-14c (400, each named), the input validation NFR-10 requires, and the
// anonymous access the endpoint depends on.
//
// Unlike UC-13, the result of this use case is directly observable — EmailVerified is a column — so
// every test reads the person row back rather than proving the change through a login. One test
// drives UC-06 and UC-14 end to end, since no response ever carries the token.
[Collection(nameof(FunctionalCollection))]
public class AuthControllerVerifyEmailTests(PostgresFixture db) : WebApiTest<Program>(EnvironmentType.Local)
{
    private const string Password = "Str0ng-Verify-Pass!";

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

    private async Task<Person> SeedPersonAsync(
        Roles role, string email, bool isDeleted = false, bool emailVerified = false)
    {
        await using var context = db.CreateContext();
        var person = new Person
        {
            PublicId = Guid.NewGuid(),
            Name = $"{role}",
            Email = email,
            PasswordHash = Hash.EncodeWithRandomSalt(Password, out var salt),
            Salt = salt,
            RoleId = (long)role,
            EmailVerified = emailVerified,
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
        await context.SaveChangesAsync();

        return person;
    }

    /// <summary>
    ///     Writes a verification token directly rather than going through person creation: these
    ///     tests are about consuming a token, and only a direct write can produce one that is already
    ///     expired or already used.
    /// </summary>
    private async Task<EmailVerificationToken> SeedTokenAsync(
        Person person, string? value = null, DateTime? expiresAt = null, bool used = false)
    {
        await using var context = db.CreateContext();
        var token = new EmailVerificationToken
        {
            PersonId = person.Id,
            Token = value ?? UniqueToken(),
            ExpiresAt = expiresAt ?? DateTime.UtcNow.AddHours(1),
            Used = used
        };
        context.EmailVerificationTokens.Add(token);
        await context.SaveChangesAsync();
        return token;
    }

    private Task<HttpOutput<DataOutput<VerifyEmailCommandOutput?>?>> VerifyAsync(string token) =>
        Gateway.PostAsync<DataOutput<VerifyEmailCommandOutput?>>(
            "/api/auth/verify-email", new VerifyEmailCommand { Token = token });

    private async Task<bool> IsVerifiedAsync(Person person)
    {
        await using var context = db.CreateContext();

        return await context.Persons
            .Where(x => x.Id == person.Id)
            .Select(x => x.EmailVerified)
            .SingleAsync();
    }

    private async Task<List<EmailVerificationToken>> TokensForAsync(Person person)
    {
        await using var context = db.CreateContext();

        return await context.EmailVerificationTokens
            .Where(token => token.PersonId == person.Id)
            .ToListAsync();
    }

    [FunctionalFact]
    public async Task GivenLiveToken_WhenPostVerifyEmail_ThenEmailIsVerifiedAndTokenConsumed()
    {
        // Given an admin holding a verification token
        var person = await SeedPersonAsync(Roles.SystemAdmin, UniqueEmail("admin"));
        var token = await SeedTokenAsync(person);

        // When
        var response = await VerifyAsync(token.Token);

        // Then — response
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(AuthMessages.EmailVerifiedSuccessfully, response.Body!.Messages);
        Assert.Empty(response.Body.Errors);

        // Then — database state: the flag is set (UC-14 step 3) and the token spent (step 4)
        Assert.True(await IsVerifiedAsync(person));
        Assert.True(Assert.Single(await TokensForAsync(person)).Used);
    }

    [FunctionalFact]
    public async Task GivenUserOfAScope_WhenPostVerifyEmail_ThenEmailIsVerified()
    {
        // Given a User, whose login needs a scope id but whose verification does not — the token
        // identifies them on its own, which is the whole reason UC-06 made it long and random
        var scope = await SeedScopeAsync();
        var person = await SeedUserAsync(scope, UniqueEmail("user"));
        var token = await SeedTokenAsync(person);

        // When
        var response = await VerifyAsync(token.Token);

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(await IsVerifiedAsync(person));
    }

    [FunctionalFact]
    public async Task GivenLiveToken_WhenPostVerifyEmail_ThenUpdatedAtIsStamped()
    {
        // Given no database trigger maintains UpdatedAt — the handler does
        var person = await SeedPersonAsync(Roles.SystemAdmin, UniqueEmail("admin"));
        var token = await SeedTokenAsync(person);

        // When
        await VerifyAsync(token.Token);

        // Then
        await using var context = db.CreateContext();
        var stored = await context.Persons.SingleAsync(x => x.Id == person.Id);
        Assert.True(stored.UpdatedAt >= stored.CreatedAt);
    }

    [FunctionalFact]
    public async Task GivenTwoLiveTokens_WhenPostVerifyEmail_ThenBothAreConsumed()
    {
        // Given someone issued a token at creation and another later, so two links work. Once one has
        // verified the address, the others verify an address that is already verified.
        var person = await SeedPersonAsync(Roles.SystemAdmin, UniqueEmail("admin"));
        var first = await SeedTokenAsync(person);
        var second = await SeedTokenAsync(person);

        // When the second link is the one clicked
        var response = await VerifyAsync(second.Token);

        // Then — both rows are spent
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.All(await TokensForAsync(person), token => Assert.True(token.Used));

        // Then — and the first link cannot be replayed (AF-14b)
        var replay = await VerifyAsync(first.Token);
        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
        Assert.Contains(AuthMessages.TokenAlreadyUsed, replay.Body!.Errors);
    }

    [FunctionalFact]
    public async Task GivenAnExpiredSiblingToken_WhenPostVerifyEmail_ThenItIsLeftAlone()
    {
        // Given a token already dead by AF-14a. Rewriting it would only make it report a different
        // reason for being dead.
        var person = await SeedPersonAsync(Roles.SystemAdmin, UniqueEmail("admin"));
        var expired = await SeedTokenAsync(person, expiresAt: DateTime.UtcNow.AddHours(-1));
        var live = await SeedTokenAsync(person);

        // When
        await VerifyAsync(live.Token);

        // Then
        var tokens = await TokensForAsync(person);
        Assert.False(tokens.Single(token => token.Id == expired.Id).Used);
        Assert.True(tokens.Single(token => token.Id == live.Id).Used);
    }

    [FunctionalFact]
    public async Task GivenAnotherPersonHoldsALiveToken_WhenPostVerifyEmail_ThenTheirTokenSurvives()
    {
        // Given the boundary of the rule above: invalidation reaches the person's own tokens and stops
        var person = await SeedPersonAsync(Roles.SystemAdmin, UniqueEmail("admin"));
        var other = await SeedPersonAsync(Roles.SystemAdmin, UniqueEmail("other"));
        await SeedTokenAsync(other);

        // When
        await VerifyAsync((await SeedTokenAsync(person)).Token);

        // Then
        Assert.False(Assert.Single(await TokensForAsync(other)).Used);
        Assert.False(await IsVerifiedAsync(other));
    }

    [FunctionalFact]
    public async Task GivenExpiredToken_WhenPostVerifyEmail_ThenBadRequestAndEmailStaysUnverified()
    {
        // Given — AF-14a (FR-EV-02)
        var person = await SeedPersonAsync(Roles.SystemAdmin, UniqueEmail("admin"));
        var token = await SeedTokenAsync(person, expiresAt: DateTime.UtcNow.AddHours(-1));

        // When
        var response = await VerifyAsync(token.Token);

        // Then — response
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(AuthMessages.TokenExpired, response.Body!.Errors);

        // Then — nothing changed
        Assert.False(await IsVerifiedAsync(person));
        Assert.False(Assert.Single(await TokensForAsync(person)).Used);
    }

    [FunctionalFact]
    public async Task GivenUsedToken_WhenPostVerifyEmail_ThenBadRequestAndEmailStaysUnverified()
    {
        // Given — AF-14b
        var person = await SeedPersonAsync(Roles.SystemAdmin, UniqueEmail("admin"));
        var token = await SeedTokenAsync(person, used: true);

        // When
        var response = await VerifyAsync(token.Token);

        // Then
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(AuthMessages.TokenAlreadyUsed, response.Body!.Errors);
        Assert.False(await IsVerifiedAsync(person));
    }

    [FunctionalFact]
    public async Task GivenUnknownToken_WhenPostVerifyEmail_ThenBadRequest()
    {
        // Given — AF-14c: nothing matches what the caller presented
        // When
        var response = await VerifyAsync(UniqueToken());

        // Then
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(AuthMessages.TokenInvalid, response.Body!.Errors);
    }

    [FunctionalFact]
    public async Task GivenTokenDifferingOnlyInCase_WhenPostVerifyEmail_ThenBadRequest()
    {
        // Given — AF-14c. The lookup is case-sensitive, unlike every email comparison in this system:
        // the token is a random secret, and folding its case would throw away part of its alphabet.
        var person = await SeedPersonAsync(Roles.SystemAdmin, UniqueEmail("admin"));
        var token = await SeedTokenAsync(person, $"MiXeD-{Guid.NewGuid():N}".ToUpper());

        // When
        var response = await VerifyAsync(token.Token.ToLower());

        // Then
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(AuthMessages.TokenInvalid, response.Body!.Errors);
        Assert.False(await IsVerifiedAsync(person));
    }

    [FunctionalTheory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GivenMissingToken_WhenPostVerifyEmail_ThenBadRequest(string token)
    {
        // Given — NFR-10. UC-14 names no alternative flow for a malformed request, and none is
        // contradicted: an absent token answers 400 here and would answer 400 as AF-14c.
        // When
        var response = await VerifyAsync(token);

        // Then
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.NotEmpty(response.Body!.Errors);
    }

    [FunctionalFact]
    public async Task GivenAlreadyVerifiedPerson_WhenPostVerifyEmail_ThenSucceedsIdempotently()
    {
        // Given an address verified before this link was clicked. UC-14 defines no alternative flow
        // for it — AF-15a rejects a *request* for another email, a different thing.
        var person = await SeedPersonAsync(Roles.SystemAdmin, UniqueEmail("admin"), emailVerified: true);
        var token = await SeedTokenAsync(person);

        // When
        var response = await VerifyAsync(token.Token);

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(AuthMessages.EmailVerifiedSuccessfully, response.Body!.Messages);
        Assert.True(await IsVerifiedAsync(person));
        Assert.True(Assert.Single(await TokensForAsync(person)).Used);
    }

    [FunctionalFact]
    public async Task GivenLogicallyDeletedPerson_WhenPostVerifyEmail_ThenEmailIsVerifiedButLoginStillFails()
    {
        // Given a deletion that landed between the email and the click. UC-14 defines no alternative
        // flow for it, and verifying grants nothing: UC-11 refuses the login by AF-11c either way.
        var email = UniqueEmail("deleted");
        var person = await SeedPersonAsync(Roles.SystemAdmin, email, isDeleted: true);
        var token = await SeedTokenAsync(person);

        // When
        var response = await VerifyAsync(token.Token);

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(await IsVerifiedAsync(person));

        var login = await Gateway.PostAsync<DataOutput<LoginCommandOutput?>>(
            "/api/auth/login", new LoginCommand { Email = email, Password = Password });

        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenNoBearerToken_WhenPostVerifyEmail_ThenEndpointAnswersAnonymously()
    {
        // Given no bearer token on the gateway: someone clicking a link in their mail client holds
        // none, and the point of the link is that they have proved nothing yet. An unknown token
        // reaches the handler and is refused there, on its merits — a 401 here would mean the
        // endpoint never ran at all.
        var response = await VerifyAsync(UniqueToken());

        // Then
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(AuthMessages.TokenInvalid, response.Body!.Errors);
    }

    [FunctionalFact]
    public async Task GivenPersonCreated_WhenVerifyingWithTheIssuedToken_ThenEmailIsVerified()
    {
        // Given the two use cases joined as a person actually meets them: UC-06 issues the token,
        // UC-14 spends it. Nothing here reaches into the database to build a token by hand.
        var email = UniqueEmail("created");
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        var creation = await Gateway.PostAsync<DataOutput<CreatePersonCommandOutput?>>(
            "/api/persons",
            new CreateAdminCommand
            {
                Name = "Created", Email = email, Password = Password, Role = (int)Roles.ScopeAdmin
            });

        Assert.Equal(HttpStatusCode.Created, creation.StatusCode);

        // The token itself never appears in a response — it is mailed. The functional suite has no
        // Mailgun credentials, so it is read back from the row UC-06 wrote.
        await using var context = db.CreateContext();
        var issued = await context.EmailVerificationTokens
            .Include(token => token.Person)
            .SingleAsync(token => token.Person.Email == email);

        Assert.False(issued.Person.EmailVerified);

        // When
        var response = await VerifyAsync(issued.Token);

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var verification = db.CreateContext();
        var stored = await verification.EmailVerificationTokens
            .Include(token => token.Person)
            .SingleAsync(token => token.Id == issued.Id);

        Assert.True(stored.Person.EmailVerified);
        Assert.True(stored.Used);
    }
}
