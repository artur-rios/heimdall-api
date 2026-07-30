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

// Functional tests for POST /api/auth/resend-verification (UC-15, FR-EV-04): the main flow for each
// of the three actors, AF-15a (400), the 401 an anonymous caller gets — the first authorization flow
// any /api/auth endpoint has — and the 404 a token outliving its person gets.
//
// The token never appears in a response, so every test reads the email_verification_token rows back
// instead: exactly one live row after a resend, and the previously outstanding ones spent. One test
// drives UC-06 → UC-15 → UC-14 end to end, proving the old link is dead and the new one works.
[Collection(nameof(FunctionalCollection))]
public class AuthControllerResendVerificationTests(PostgresFixture db)
    : WebApiTest<Program>(EnvironmentType.Local)
{
    private const string Password = "Str0ng-Resend-Pass!";

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
    ///     Writes a verification token directly rather than through person creation: only a direct
    ///     write can produce one that is already expired or already spent, and these tests are about
    ///     what a resend does to the tokens that already exist.
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

    /// <summary>
    ///     The request carries no body — the person comes from the bearer token — but
    ///     <c>HttpGateway.PostAsync</c> takes a payload, so an empty object is sent and the action,
    ///     which binds no body, ignores it.
    /// </summary>
    private Task<HttpOutput<DataOutput<ResendVerificationEmailCommandOutput?>?>> ResendAsync() =>
        Gateway.PostAsync<DataOutput<ResendVerificationEmailCommandOutput?>>(
            "/api/auth/resend-verification", new { });

    /// <summary>
    ///     Switches the gateway to another caller's token. <c>Authorize</c> adds the header rather than
    ///     replacing it, and <c>Authorization</c> permits a single value, so a test that acts as two
    ///     people in turn — an admin creating a person, then that person resending — has to clear the
    ///     first one. This is the suite's first such test.
    /// </summary>
    private void Reauthorize(string authToken)
    {
        Gateway.Client.DefaultRequestHeaders.Remove("Authorization");
        Authorize(authToken);
    }

    private async Task<List<EmailVerificationToken>> TokensForAsync(Person person)
    {
        await using var context = db.CreateContext();

        return await context.EmailVerificationTokens
            .Where(token => token.PersonId == person.Id)
            .ToListAsync();
    }

    private async Task<EmailVerificationToken> SingleLiveTokenAsync(Person person)
    {
        var live = (await TokensForAsync(person))
            .Where(token => !token.Used && token.ExpiresAt > DateTime.UtcNow)
            .ToList();

        return Assert.Single(live);
    }

    [FunctionalFact]
    public async Task GivenAuthenticatedSystemAdmin_WhenPostResendVerification_ThenNewTokenIsIssued()
    {
        // Given an unverified System Admin holding no live link
        var person = await SeedPersonAsync(Roles.SystemAdmin, UniqueEmail("admin"));
        Authorize(TestTokens.For(person.PublicId, (int)Roles.SystemAdmin));

        // When
        var response = await ResendAsync();

        // Then — response
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(AuthMessages.VerificationEmailSent, response.Body!.Messages);
        Assert.Empty(response.Body.Errors);

        // Then — database state: UC-15 steps 4 and 5 wrote a live token, and it is not the caller's
        // to see — no response ever carries it
        var issued = await SingleLiveTokenAsync(person);
        Assert.True(issued.ExpiresAt > DateTime.UtcNow);
        Assert.False(string.IsNullOrWhiteSpace(issued.Token));
    }

    [FunctionalFact]
    public async Task GivenAuthenticatedScopeAdmin_WhenPostResendVerification_ThenNewTokenIsIssued()
    {
        // Given the authorization matrix grants email verification to all three roles
        var person = await SeedPersonAsync(Roles.ScopeAdmin, UniqueEmail("scope-admin"));
        Authorize(TestTokens.For(person.PublicId, (int)Roles.ScopeAdmin));

        // When
        var response = await ResendAsync();

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await SingleLiveTokenAsync(person);
    }

    [FunctionalFact]
    public async Task GivenAuthenticatedUserOfAScope_WhenPostResendVerification_ThenNewTokenIsIssued()
    {
        // Given a User, whose login needs a scope id but whose resend does not — the endpoint reads
        // them from their own token and nothing else
        var scope = await SeedScopeAsync();
        var person = await SeedUserAsync(scope, UniqueEmail("user"));
        Authorize(TestTokens.For(person.PublicId, (int)Roles.User, scope.PublicId));

        // When
        var response = await ResendAsync();

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await SingleLiveTokenAsync(person);
    }

    [FunctionalFact]
    public async Task GivenOutstandingLiveToken_WhenPostResendVerification_ThenItIsRetiredAndOnlyTheNewOneIsLive()
    {
        // Given — UC-15 step 3. After a resend only the newest link works.
        var person = await SeedPersonAsync(Roles.SystemAdmin, UniqueEmail("admin"));
        var outstanding = await SeedTokenAsync(person);
        Authorize(TestTokens.For(person.PublicId, (int)Roles.SystemAdmin));

        // When
        var response = await ResendAsync();

        // Then — the old row is spent and exactly one live row remains
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var tokens = await TokensForAsync(person);
        Assert.Equal(2, tokens.Count);
        Assert.True(tokens.Single(token => token.Id == outstanding.Id).Used);

        var issued = await SingleLiveTokenAsync(person);
        Assert.NotEqual(outstanding.Token, issued.Token);

        // Then — and the retired link cannot be spent (UC-14 AF-14b)
        var replay = await Gateway.PostAsync<DataOutput<VerifyEmailCommandOutput?>>(
            "/api/auth/verify-email", new VerifyEmailCommand { Token = outstanding.Token });

        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
        Assert.Contains(AuthMessages.TokenAlreadyUsed, replay.Body!.Errors);
    }

    [FunctionalFact]
    public async Task GivenAnExpiredToken_WhenPostResendVerification_ThenItIsLeftAlone()
    {
        // Given a token already dead by AF-14a. Rewriting it would only make it report a different
        // reason for being dead — the same boundary UC-14 draws.
        var person = await SeedPersonAsync(Roles.SystemAdmin, UniqueEmail("admin"));
        var expired = await SeedTokenAsync(person, expiresAt: DateTime.UtcNow.AddHours(-1));
        Authorize(TestTokens.For(person.PublicId, (int)Roles.SystemAdmin));

        // When
        await ResendAsync();

        // Then
        Assert.False((await TokensForAsync(person)).Single(token => token.Id == expired.Id).Used);
    }

    [FunctionalFact]
    public async Task GivenAnotherPersonHoldsALiveToken_WhenPostResendVerification_ThenTheirTokenSurvives()
    {
        // Given the boundary of the retirement rule: it reaches the caller's own tokens and stops
        var person = await SeedPersonAsync(Roles.SystemAdmin, UniqueEmail("admin"));
        var other = await SeedPersonAsync(Roles.SystemAdmin, UniqueEmail("other"));
        await SeedTokenAsync(other);
        Authorize(TestTokens.For(person.PublicId, (int)Roles.SystemAdmin));

        // When
        await ResendAsync();

        // Then
        Assert.False(Assert.Single(await TokensForAsync(other)).Used);
    }

    [FunctionalFact]
    public async Task GivenAlreadyVerifiedPerson_WhenPostResendVerification_ThenBadRequestAndNoTokenIsIssued()
    {
        // Given — AF-15a: a link mailed to a verified address could do nothing when clicked
        var person = await SeedPersonAsync(Roles.SystemAdmin, UniqueEmail("admin"), emailVerified: true);
        var outstanding = await SeedTokenAsync(person);
        Authorize(TestTokens.For(person.PublicId, (int)Roles.SystemAdmin));

        // When
        var response = await ResendAsync();

        // Then — response
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(AuthMessages.EmailAlreadyVerified, response.Body!.Errors);

        // Then — nothing was issued and nothing retired: AF-15a is checked before UC-15 step 3
        var tokens = await TokensForAsync(person);
        Assert.False(Assert.Single(tokens).Used);
        Assert.Equal(outstanding.Token, tokens.Single().Token);
    }

    [FunctionalFact]
    public async Task GivenNoBearerToken_WhenPostResendVerification_ThenUnauthorized()
    {
        // Given no bearer token on the gateway. Unlike UC-11…UC-14, this endpoint is not anonymous:
        // there is no other way to say whose address should be verified.
        var response = await ResendAsync();

        // Then
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenTokenNamingNoExistingPerson_WhenPostResendVerification_ThenNotFound()
    {
        // Given a well-formed token for a person who is not in the database. Authentication runs in
        // ClaimsOnly mode — no read per request — so this is what a hard deletion (UC-10) leaves
        // behind: a valid token and no address to send to.
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        // When
        var response = await ResendAsync();

        // Then
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains(AuthMessages.PersonNotFound, response.Body!.Errors);
    }

    [FunctionalFact]
    public async Task GivenLogicallyDeletedPerson_WhenPostResendVerification_ThenNewTokenIsIssued()
    {
        // Given a deletion that landed after the caller's bearer token was issued. UC-15 defines
        // exactly one alternative flow, so refusing here would be inventing a second — and verifying
        // grants nothing on its own, since UC-11 refuses the login by AF-11c either way.
        var email = UniqueEmail("deleted");
        var person = await SeedPersonAsync(Roles.SystemAdmin, email, isDeleted: true);
        Authorize(TestTokens.For(person.PublicId, (int)Roles.SystemAdmin));

        // When
        var response = await ResendAsync();

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await SingleLiveTokenAsync(person);

        // Then — and it buys them nothing: the login is still refused
        var login = await Gateway.PostAsync<DataOutput<LoginCommandOutput?>>(
            "/api/auth/login", new LoginCommand { Email = email, Password = Password });

        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenPersonCreated_WhenResendingThenVerifying_ThenOldLinkIsDeadAndNewOneWorks()
    {
        // Given the three use cases joined as a person actually meets them: UC-06 issues a link,
        // UC-15 replaces it, UC-14 spends the replacement. Nothing here builds a token by hand.
        var email = UniqueEmail("created");
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        var creation = await Gateway.PostAsync<DataOutput<CreatePersonCommandOutput?>>(
            "/api/persons",
            new CreateAdminCommand
            {
                Name = "Created", Email = email, Password = Password, Role = (int)Roles.ScopeAdmin
            });

        Assert.Equal(HttpStatusCode.Created, creation.StatusCode);

        var created = await PersonByEmailAsync(email);
        var original = Assert.Single(await TokensForAsync(created));

        // When the person asks for a new link, as themselves
        Reauthorize(TestTokens.For(created.PublicId, (int)Roles.ScopeAdmin));

        var response = await ResendAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Then — UC-06's link is dead (UC-14 AF-14b)
        var reissued = await SingleLiveTokenAsync(created);

        var replay = await Gateway.PostAsync<DataOutput<VerifyEmailCommandOutput?>>(
            "/api/auth/verify-email", new VerifyEmailCommand { Token = original.Token });

        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
        Assert.Contains(AuthMessages.TokenAlreadyUsed, replay.Body!.Errors);

        // Then — and the reissued one verifies the address
        var verification = await Gateway.PostAsync<DataOutput<VerifyEmailCommandOutput?>>(
            "/api/auth/verify-email", new VerifyEmailCommand { Token = reissued.Token });

        Assert.Equal(HttpStatusCode.OK, verification.StatusCode);

        await using var context = db.CreateContext();
        Assert.True(await context.Persons
            .Where(person => person.Id == created.Id)
            .Select(person => person.EmailVerified)
            .SingleAsync());
    }

    private async Task<Person> PersonByEmailAsync(string email)
    {
        await using var context = db.CreateContext();

        return await context.Persons.AsNoTracking().SingleAsync(person => person.Email == email);
    }
}
