using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ArturRios.Configuration.Enums;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Command.Services;
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

// Functional tests for POST /api/auth/resend-verification (UC-15, FR-EV-04): the main flow for each
// of the three actors, AF-15a (400), AF-15b (429, the person's own account email went out within the
// cooldown, FR-PE-16), the credential endpoints' rate limit, the 401 an anonymous caller gets — the first
// authorization flow any /api/auth endpoint has — and the 404 a token outliving its person gets.
//
// The token never appears in a response, so every test reads the email_verification_token rows back
// instead: exactly one live row after a resend, and the previously outstanding ones spent. Since
// TH-14 those rows hold only a digest, so a token a test seeded is recognised in them by hashing it
// again, and a token the API issued is recognised only as "the live one" — which is all UC-15's
// contract is about.
[Collection(nameof(FunctionalCollection))]
public class AuthControllerResendVerificationTests(PostgresFixture db)
    : WebApiTest<Program>(EnvironmentType.Local)
{
    private const string Password = Seeds.Password;

    private static string UniqueEmail(string prefix) => $"{prefix}-{Guid.NewGuid():N}@test.local";

    private static string UniqueToken() => $"token-{Guid.NewGuid():N}";

    /// <summary>
    ///     Writes a verification token directly rather than through person creation: only a direct
    ///     write can produce one that is already expired or already spent, and these tests are about
    ///     what a resend does to the tokens that already exist.
    /// </summary>
    private async Task<string> SeedTokenAsync(
        Person person, string? value = null, DateTime? expiresAt = null, bool used = false)
    {
        var plaintext = value ?? UniqueToken();

        await using var context = db.CreateContext();
        var token = new EmailVerificationToken
        {
            PersonId = person.Id,
            TokenHash = SingleUseTokenHash.Of(plaintext),
            ExpiresAt = expiresAt ?? DateTime.UtcNow.AddHours(1),
            Used = used
        };
        context.EmailVerificationTokens.Add(token);
        await context.SaveChangesAsync();
        return plaintext;
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

    private async Task<List<EmailVerificationToken>> LiveTokensAsync(Person person) =>
        (await TokensForAsync(person))
        .Where(token => !token.Used && token.ExpiresAt > DateTime.UtcNow)
        .ToList();

    private async Task<EmailVerificationToken> SingleLiveTokenAsync(Person person) =>
        Assert.Single(await LiveTokensAsync(person));

    [FunctionalFact]
    public async Task GivenAuthenticatedSystemAdmin_WhenPostResendVerification_ThenNewTokenIsIssued()
    {
        // Given an unverified System Admin holding no live link
        var person = await Seeds.PersonAsync(db, Roles.SystemAdmin, new PersonOptions { Email = UniqueEmail("admin"), EmailVerified = false });
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
        Assert.Equal(SingleUseTokenHash.Length, issued.TokenHash.Length);
    }

    [FunctionalFact]
    public async Task GivenAuthenticatedScopeAdmin_WhenPostResendVerification_ThenNewTokenIsIssued()
    {
        // Given the authorization matrix grants email verification to all three roles
        var person = await Seeds.PersonAsync(db, Roles.ScopeAdmin, new PersonOptions { Email = UniqueEmail("scope-admin"), EmailVerified = false });
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
        var scope = await Seeds.ScopeAsync(db);
        var person = await Seeds.PersonAsync(db, Roles.User, new PersonOptions { Email = UniqueEmail("user"), EmailVerified = false, MemberOf = scope });
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
        var person = await Seeds.PersonAsync(db, Roles.SystemAdmin, new PersonOptions { Email = UniqueEmail("admin"), EmailVerified = false });
        var outstanding = await SeedTokenAsync(person);
        Authorize(TestTokens.For(person.PublicId, (int)Roles.SystemAdmin));

        // When
        var response = await ResendAsync();

        // Then — the old row is spent and exactly one live row remains
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var tokens = await TokensForAsync(person);
        Assert.Equal(2, tokens.Count);
        var outstandingHash = SingleUseTokenHash.Of(outstanding);
        Assert.True(tokens.Single(token => token.TokenHash == outstandingHash).Used);

        var issued = await SingleLiveTokenAsync(person);
        Assert.NotEqual(outstandingHash, issued.TokenHash);

        // Then — and the retired link cannot be spent (UC-14 AF-14b)
        var replay = await Gateway.PostAsync<DataOutput<VerifyEmailCommandOutput?>>(
            "/api/auth/verify-email", new VerifyEmailCommand { Token = outstanding });

        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
        Assert.Contains(AuthMessages.TokenAlreadyUsed, replay.Body!.Errors);
    }

    [FunctionalFact]
    public async Task GivenAnExpiredToken_WhenPostResendVerification_ThenItIsLeftAlone()
    {
        // Given a token already dead by AF-14a. Rewriting it would only make it report a different
        // reason for being dead — the same boundary UC-14 draws.
        var person = await Seeds.PersonAsync(db, Roles.SystemAdmin, new PersonOptions { Email = UniqueEmail("admin"), EmailVerified = false });
        var expired = await SeedTokenAsync(person, expiresAt: DateTime.UtcNow.AddHours(-1));
        Authorize(TestTokens.For(person.PublicId, (int)Roles.SystemAdmin));

        // When
        await ResendAsync();

        // Then
        var expiredHash = SingleUseTokenHash.Of(expired);

        Assert.False((await TokensForAsync(person))
            .Single(token => token.TokenHash == expiredHash).Used);
    }

    [FunctionalFact]
    public async Task GivenAnotherPersonHoldsALiveToken_WhenPostResendVerification_ThenTheirTokenSurvives()
    {
        // Given the boundary of the retirement rule: it reaches the caller's own tokens and stops
        var person = await Seeds.PersonAsync(db, Roles.SystemAdmin, new PersonOptions { Email = UniqueEmail("admin"), EmailVerified = false });
        var other = await Seeds.PersonAsync(db, Roles.SystemAdmin, new PersonOptions { Email = UniqueEmail("other"), EmailVerified = false });
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
        var person = await Seeds.PersonAsync(db, Roles.SystemAdmin, new PersonOptions { Email = UniqueEmail("admin") });
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
        Assert.Equal(SingleUseTokenHash.Of(outstanding), tokens.Single().TokenHash);
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
        // Given a well-formed token for a person who is not in the database — what a hard deletion
        // (UC-10) leaves behind, since authentication itself reads no data store.
        Authorize(TestTokens.For(Guid.NewGuid(), (int)Roles.SystemAdmin));

        // When
        var response = await ResendAsync();

        // Then — ActorLivenessFilter refuses it before the handler runs, so this is a 401 rather
        // than the handler's own 404. That is the stronger answer and the uniform one: a token
        // naming nobody is not a request that failed to find something, it is a token the API will
        // not honour anywhere.
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(ActorLivenessFilter.ActorNotLive, response.Body!.Errors);
    }

    [FunctionalFact]
    public async Task GivenLogicallyDeletedPerson_WhenPostResendVerification_ThenUnauthorized()
    {
        // Given a deletion that landed after the caller's bearer token was issued
        var email = UniqueEmail("deleted");
        var person = await Seeds.PersonAsync(db, Roles.SystemAdmin, new PersonOptions { Email = email, EmailVerified = false, IsDeleted = true });
        Authorize(TestTokens.For(person.PublicId, (int)Roles.SystemAdmin));

        // When
        var response = await ResendAsync();

        // Then — refused. This endpoint used to serve a logically deleted person, on the reasoning
        // that UC-15 defines one alternative flow and a verified address grants nothing on its own.
        // ActorLivenessFilter overrides that: FR-AU-05 says a deleted person cannot authenticate,
        // and letting their token keep working everywhere except login made that true of one
        // endpoint rather than of the account. Nothing is issued.
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(ActorLivenessFilter.ActorNotLive, response.Body!.Errors);
        Assert.Empty(await LiveTokensAsync(person));

        // Then — and the login they were deleted out of is still refused
        var login = await Gateway.PostAsync<DataOutput<LoginCommandOutput?>>(
            "/api/auth/login", new LoginCommand { Email = email, Password = Password });

        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenPersonCreated_WhenResending_ThenTheOldTokenIsRetiredAndAFreshOneIssued()
    {
        // UC-06 issues a link and UC-15 replaces it. This used to go one step further and have UC-14
        // spend the replacement, which needed both tokens in plaintext — and since TH-14 neither is
        // readable from the database, with no inbox in this suite to read them from instead.
        //
        // What is still observable is the whole of UC-15's contract, and it is not a small residue:
        // the token UC-06 issued is retired, exactly one live token remains, and it is a different
        // one. Whether a live token can then be spent is UC-14's own question, covered by
        // VerifyEmailCommandHandlerTests where the plaintext is in the test's hands.
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

        // When the person asks for a new link, as themselves — at once: the link UC-06 sent is the
        // administrator's mail, not charged to the person's own budget (FR-PE-16)
        Reauthorize(TestTokens.For(created.PublicId, (int)Roles.ScopeAdmin));

        var response = await ResendAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Then — UC-06's token is retired (UC-15 step 3), so the link in the first email is dead
        var all = await TokensForAsync(created);

        Assert.Equal(2, all.Count);
        Assert.True(all.Single(token => token.TokenHash.SequenceEqual(original.TokenHash)).Used);

        // Then — and exactly one live token remains, which is not the retired one
        var reissued = await SingleLiveTokenAsync(created);

        Assert.NotEqual(original.TokenHash, reissued.TokenHash);
        Assert.Equal(SingleUseTokenHash.Length, reissued.TokenHash.Length);
        Assert.True(reissued.ExpiresAt > DateTime.UtcNow);

        // Then — and the address is still unverified: replacing a link verifies nothing by itself
        await using var context = db.CreateContext();
        Assert.False(await context.Persons
            .Where(person => person.Id == created.Id)
            .Select(person => person.EmailVerified)
            .SingleAsync());
    }

    [FunctionalFact]
    public async Task GivenALinkWasJustMailed_WhenPostResendVerificationAgain_ThenTooManyRequestsAndTheLinkIsKept()
    {
        // Given — AF-15b: a resend a moment ago
        var person = await Seeds.PersonAsync(db, Roles.User, new PersonOptions { Email = UniqueEmail("cooldown"), EmailVerified = false });
        Authorize(TestTokens.For(person.PublicId, (int)Roles.User));

        Assert.Equal(HttpStatusCode.OK, (await ResendAsync()).StatusCode);
        var mailed = await SingleLiveTokenAsync(person);

        // When
        var response = await ResendAsync();

        // Then — refused with the cooldown's answer, and the link just mailed still works
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Contains(AuthMessages.EmailSentRecently, response.Body!.Errors);
        Assert.Equal(mailed.TokenHash, (await SingleLiveTokenAsync(person)).TokenHash);
    }

    [FunctionalFact]
    public async Task GivenTheRateLimitIsSpent_WhenPostResendVerification_ThenTooManyRequestsFromTheLimiter()
    {
        // Given ten requests from one client address. The first sends; the cooldown answers the rest,
        // so the limiter is what this test reaches. A trusted caller's named address gives the test a
        // bucket of its own.
        var person = await Seeds.PersonAsync(db, Roles.User, new PersonOptions { Email = UniqueEmail("limited"), EmailVerified = false });
        Authorize(TestTokens.For(person.PublicId, (int)Roles.User));

        for (var attempt = 1; attempt <= 10; attempt++)
        {
            using var allowed = await ResendFromAsync("203.0.113.61");
            Assert.NotEqual(RateLimitMessages.TooManyRequests, await FirstErrorAsync(allowed));
        }

        // When
        using var response = await ResendFromAsync("203.0.113.61");

        // Then
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.NotNull(response.Headers.RetryAfter);
        Assert.Equal(RateLimitMessages.TooManyRequests, await FirstErrorAsync(response));
    }

    private async Task<HttpResponseMessage> ResendFromAsync(string clientAddress)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/resend-verification")
        {
            Content = JsonContent.Create(new { })
        };

        request.Headers.Add(AuthRateLimitPartition.CallerKeyHeader, PostgresFixture.TrustedCallerKey);
        request.Headers.Add(AuthRateLimitPartition.ClientAddressHeader, clientAddress);

        return await Gateway.Client.SendAsync(request);
    }

    private static async Task<string?> FirstErrorAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        return document.RootElement.TryGetProperty("errors", out var errors)
            ? errors.EnumerateArray().Select(error => error.GetString()).FirstOrDefault()
            : null;
    }

    private async Task<Person> PersonByEmailAsync(string email)
    {
        await using var context = db.CreateContext();

        return await context.Persons.AsNoTracking().SingleAsync(person => person.Email == email);
    }
}
