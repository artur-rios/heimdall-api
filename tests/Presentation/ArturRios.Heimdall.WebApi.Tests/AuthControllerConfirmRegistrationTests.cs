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
using ArturRios.Util.Hashing;
using ArturRios.Util.Http;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Functional;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Heimdall.WebApi.Tests;

// Functional tests for confirming a UC-47 pending registration through POST /api/auth/verify-email
// (UC-14). The wire contract is the verify-email one; what is new is that a token matching no
// verification token may name a pending registration, whose confirmation creates the verified User.
// The other half is the reason pending registrations exist: before the link is followed there is no
// Person, so signing in must answer exactly as a wrong password does.
[Collection(nameof(FunctionalCollection))]
public class AuthControllerConfirmRegistrationTests(PostgresFixture db) : WebApiTest<Program>(EnvironmentType.Local)
{
    private const string Password = "Str0ng-Confirm-Pass!";

    private static string UniqueEmail(string prefix) => $"{prefix}-{Guid.NewGuid():N}@test.local";

    private async Task<Scope> SeedScopeAsync(bool open = true)
    {
        await using var context = db.CreateContext();
        var scope = new Scope
        {
            PublicId = Guid.NewGuid(), Name = $"scope-{Guid.NewGuid():N}", SelfRegistrationEnabled = open
        };
        context.Scopes.Add(scope);
        await context.SaveChangesAsync();
        return scope;
    }

    private async Task<Person> SeedUserAsync(Scope scope, string email)
    {
        await using var context = db.CreateContext();
        var person = new Person
        {
            PublicId = Guid.NewGuid(), Name = "Existing", Email = email,
            PasswordHash = Hash.EncodeWithRandomSalt(Password, out var salt), Salt = salt,
            RoleId = (long)Roles.User, EmailVerified = true
        };
        context.Persons.Add(person);
        await context.SaveChangesAsync();

        context.ScopeUsers.Add(new ScopeUser { ScopeId = scope.Id, PersonId = person.Id });
        person.ScopeId = scope.Id;
        await context.SaveChangesAsync();

        return person;
    }

    /// <summary>Writes a pending registration directly, so the test holds the plaintext token.</summary>
    private async Task<string> SeedPendingAsync(
        Scope scope, string email, TimeSpan? lifetime = null, int failedAttempts = 0)
    {
        var token = SingleUseToken.New();

        await using var context = db.CreateContext();
        context.PendingRegistrations.Add(new PendingRegistration
        {
            ScopeId = scope.Id,
            Name = "Confirmed Person",
            Email = email,
            PasswordHash = Hash.EncodeWithRandomSalt(Password, out var salt),
            Salt = salt,
            TokenHash = SingleUseTokenHash.Of(token),
            ExpiresAt = DateTime.UtcNow.Add(lifetime ?? TimeSpan.FromHours(1)),
            CreatedAt = DateTime.UtcNow,
            FailedAttempts = failedAttempts
        });
        await context.SaveChangesAsync();

        return token;
    }

    private Task<HttpOutput<DataOutput<VerifyEmailCommandOutput?>?>> VerifyAsync(
        string token, string? password = Password) =>
        Gateway.PostAsync<DataOutput<VerifyEmailCommandOutput?>>(
            "/api/auth/verify-email", new VerifyEmailCommand { Token = token, Password = password });

    // A body with no password field at all, as a client that never sends one would post.
    private Task<HttpOutput<DataOutput<VerifyEmailCommandOutput?>?>> VerifyWithoutPasswordFieldAsync(
        string token) =>
        Gateway.PostAsync<DataOutput<VerifyEmailCommandOutput?>>("/api/auth/verify-email", new { token });

    /// <summary>
    ///     Starts <paramref name="count" /> copies of a request at the same moment and waits for all of
    ///     them. The burst runs under a hash gate with a patient wait, so a slow runner's queue of
    ///     derivations is not refused with 503: that is the gate's policy, not what is tested here.
    /// </summary>
    private static async Task<T[]> InParallelAsync<T>(int count, Func<int, Task<T>> request)
    {
        var original = PasswordHashGate.Shared;
        PasswordHashGate.Shared = new PasswordHashGate(original.MaxConcurrent, TimeSpan.FromMinutes(2));

        try
        {
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var requests = Enumerable.Range(0, count)
                .Select(index => Task.Run(async () =>
                {
                    await start.Task;
                    return await request(index);
                }))
                .ToArray();

            start.SetResult();

            return await Task.WhenAll(requests);
        }
        finally
        {
            PasswordHashGate.Shared = original;
        }
    }

    private async Task<int?> FailedAttemptsAsync(Scope scope, string email)
    {
        await using var context = db.CreateContext();

        return await context.PendingRegistrations
            .Where(pending => pending.ScopeId == scope.Id && pending.Email == email)
            .Select(pending => (int?)pending.FailedAttempts)
            .SingleOrDefaultAsync();
    }

    private Task<HttpOutput<DataOutput<LoginCommandOutput?>?>> LoginAsync(
        string email, Guid scopeId, string password = Password) =>
        Gateway.PostAsync<DataOutput<LoginCommandOutput?>>(
            "/api/auth/login", new LoginCommand { Email = email, Password = password, ScopeId = scopeId });

    private Task<HttpOutput<DataOutput<RegisterCommandOutput?>?>> RegisterAsync(string email, Guid scopeId) =>
        Gateway.PostAsync<DataOutput<RegisterCommandOutput?>>(
            "/api/auth/register",
            new RegisterCommand { Name = "New Person", Email = email, Password = Password, ScopeId = scopeId });

    private async Task<List<Person>> PersonsAsync(Scope scope, string email)
    {
        await using var context = db.CreateContext();

        return await context.Persons
            .Where(person => person.ScopeId == scope.Id && person.Email.ToLower() == email.ToLower())
            .ToListAsync();
    }

    private async Task<int> PendingCountAsync(Scope scope, string email)
    {
        await using var context = db.CreateContext();

        return await context.PendingRegistrations
            .CountAsync(pending => pending.ScopeId == scope.Id && pending.Email.ToLower() == email.ToLower());
    }

    /// <summary>
    ///     Waits until a registration made first has been worked through, by polling for its pending row
    ///     until it appears or 30 seconds pass.
    /// </summary>
    private async Task WaitForPendingAsync(Scope scope, string email)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);

        while (await PendingCountAsync(scope, email) == 0)
        {
            Assert.True(DateTime.UtcNow < deadline, "The registration queue did not settle in time.");
            await Task.Delay(50);
        }
    }

    [FunctionalFact]
    public async Task GivenAPendingRegistrationToken_WhenPostVerifyEmail_ThenAVerifiedUserExistsAndCanSignIn()
    {
        var scope = await SeedScopeAsync();
        var email = UniqueEmail("pending");
        var token = await SeedPendingAsync(scope, email);

        var response = await VerifyAsync(token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(AuthMessages.EmailVerifiedSuccessfully, response.Body!.Messages);
        Assert.Empty(response.Body.Errors);

        var person = Assert.Single(await PersonsAsync(scope, email));
        Assert.True(person.EmailVerified);
        Assert.Equal((long)Roles.User, person.RoleId);
        Assert.Equal("Confirmed Person", person.Name);
        Assert.Equal(0, await PendingCountAsync(scope, email));

        var login = await LoginAsync(email, scope.PublicId);

        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.False(string.IsNullOrEmpty(login.Body!.Data!.Token));
    }

    [FunctionalFact]
    public async Task GivenAPendingRegistrationToken_WhenPostedWithoutAPassword_ThenUnprocessableAndNothingIsCreated()
    {
        var scope = await SeedScopeAsync();
        var email = UniqueEmail("nopassword");
        var token = await SeedPendingAsync(scope, email);

        var response = await VerifyWithoutPasswordFieldAsync(token);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains(AuthMessages.ConfirmationPasswordRequired, response.Body!.Errors);
        Assert.Equal(1, await PendingCountAsync(scope, email));
        Assert.Empty(await PersonsAsync(scope, email));
    }

    [FunctionalFact]
    public async Task GivenAPendingRegistrationToken_WhenPostedWithAWrongPassword_ThenUnauthorizedAndTheRowIsKept()
    {
        var scope = await SeedScopeAsync();
        var email = UniqueEmail("wrongpassword");
        var token = await SeedPendingAsync(scope, email);

        var response = await VerifyAsync(token, "wrong");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(AuthMessages.ConfirmationPasswordIncorrect, response.Body!.Errors);
        Assert.Equal(1, await PendingCountAsync(scope, email));
        Assert.Empty(await PersonsAsync(scope, email));
    }

    [FunctionalFact]
    public async Task GivenWrongPasswords_WhenPostedForAPendingRegistration_ThenEachIsCountedOnTheRow()
    {
        var scope = await SeedScopeAsync();
        var email = UniqueEmail("counted");
        var token = await SeedPendingAsync(scope, email);

        await VerifyAsync(token, "wrong-one");
        await VerifyAsync(token, "wrong-two");

        Assert.Equal(2, await FailedAttemptsAsync(scope, email));
    }

    [FunctionalFact]
    public async Task GivenNineWrongPasswords_WhenTheTenthIsPosted_ThenTheTokenStopsWorking()
    {
        // Nine are seeded rather than posted: the auth endpoints allow ten requests a minute per address.
        var scope = await SeedScopeAsync();
        var email = UniqueEmail("capped");
        var token = await SeedPendingAsync(scope, email, failedAttempts: 9);

        var tenth = await VerifyAsync(token, "wrong");
        var right = await VerifyAsync(token);

        Assert.Equal(HttpStatusCode.Unauthorized, tenth.StatusCode);
        Assert.Contains(AuthMessages.ConfirmationPasswordIncorrect, tenth.Body!.Errors);
        Assert.Equal(HttpStatusCode.BadRequest, right.StatusCode);
        Assert.Contains(AuthMessages.TokenInvalid, right.Body!.Errors);
        Assert.Equal(0, await PendingCountAsync(scope, email));
        Assert.Empty(await PersonsAsync(scope, email));
    }

    [FunctionalFact]
    public async Task GivenFourGuessesLeft_WhenTenWrongPasswordsArriveAtOnce_ThenExactlyFourAreComparedAndTheRowIsGone()
    {
        // Given a registration with four of its ten guesses left (FR-SR-09), and a burst of ten: as many
        // as the per-address limit admits, so every request reaches the confirmer and the cap is the only
        // thing that can refuse. Counted after the comparison, the ten all read six and all wrote seven,
        // and every one of them was compared and answered 401.
        var scope = await SeedScopeAsync();
        var email = UniqueEmail("burst");
        var token = await SeedPendingAsync(scope, email, failedAttempts: 6);

        // When ten wrong passwords arrive together
        var responses = await InParallelAsync(10, _ => VerifyAsync(token, "Wr0ng-Confirm-Pass!"));

        // Then exactly four were compared and refused as wrong, the other six were refused as an invalid
        // token without being compared, and the registration is gone
        var compared = responses.Where(response => response.StatusCode == HttpStatusCode.Unauthorized).ToList();
        var refused = responses.Where(response => response.StatusCode == HttpStatusCode.BadRequest).ToList();

        Assert.Equal(4, compared.Count);
        Assert.All(compared, response => Assert.Contains(AuthMessages.ConfirmationPasswordIncorrect, response.Body!.Errors));
        Assert.Equal(6, refused.Count);
        Assert.All(refused, response => Assert.Contains(AuthMessages.TokenInvalid, response.Body!.Errors));
        Assert.Equal(0, await PendingCountAsync(scope, email));
        Assert.Empty(await PersonsAsync(scope, email));
    }

    [FunctionalFact]
    public async Task GivenTheGuessBudgetIsSpent_WhenTheRightPasswordIsPosted_ThenItIsRefusedWithoutBeingCompared()
    {
        // Given a row whose ten guesses were all charged but which a lost delete left behind
        var scope = await SeedScopeAsync();
        var email = UniqueEmail("spent");
        var token = await SeedPendingAsync(scope, email, failedAttempts: 10);

        // When the right password arrives
        var response = await VerifyAsync(token);

        // Then it is answered as an invalid token, the row is discarded and nobody is created
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(AuthMessages.TokenInvalid, response.Body!.Errors);
        Assert.Equal(0, await PendingCountAsync(scope, email));
        Assert.Empty(await PersonsAsync(scope, email));
    }

    [FunctionalFact]
    public async Task GivenAPendingRegistrationToken_WhenPostedTwice_ThenTheSecondIsInvalidAndThereIsOnePerson()
    {
        var scope = await SeedScopeAsync();
        var email = UniqueEmail("twice");
        var token = await SeedPendingAsync(scope, email);

        var first = await VerifyAsync(token);
        var second = await VerifyAsync(token);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);
        Assert.Contains(AuthMessages.TokenInvalid, second.Body!.Errors);
        Assert.Single(await PersonsAsync(scope, email));
    }

    [FunctionalFact]
    public async Task GivenAnExpiredPendingRegistrationToken_WhenPostVerifyEmail_ThenTokenExpiredAndNoPerson()
    {
        var scope = await SeedScopeAsync();
        var email = UniqueEmail("expired");
        var token = await SeedPendingAsync(scope, email, TimeSpan.FromMinutes(-5));

        var response = await VerifyAsync(token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(AuthMessages.TokenExpired, response.Body!.Errors);
        Assert.Empty(await PersonsAsync(scope, email));
    }

    [FunctionalFact]
    public async Task GivenARegisteredAddressNotYetConfirmed_WhenLoggingIn_ThenTheAnswerIsTheOneAWrongPasswordGets()
    {
        // Given a free address registered through the API (so only a pending registration exists) and
        // an existing User whose password is about to be got wrong
        var scope = await SeedScopeAsync();
        var email = UniqueEmail("unconfirmed");
        var existingEmail = UniqueEmail("existing");
        await SeedUserAsync(scope, existingEmail);

        var registration = await RegisterAsync(email, scope.PublicId);
        await WaitForPendingAsync(scope, email);

        // When signing in with the new address and its own password, and with the existing address
        // and a wrong one
        var unconfirmed = await LoginAsync(email, scope.PublicId);
        var wrongPassword = await LoginAsync(existingEmail, scope.PublicId, "Wr0ng-Confirm-Pass!");

        // Then the two cannot be told apart, and no Person exists to have signed in
        Assert.Equal(HttpStatusCode.Accepted, registration.StatusCode);
        Assert.Empty(await PersonsAsync(scope, email));
        Assert.Equal(HttpStatusCode.Unauthorized, unconfirmed.StatusCode);
        Assert.Contains(AuthMessages.InvalidCredentials, unconfirmed.Body!.Errors);
        Assert.Equal(wrongPassword.StatusCode, unconfirmed.StatusCode);
        Assert.Equal(wrongPassword.Body!.Errors, unconfirmed.Body.Errors);
        Assert.Null(unconfirmed.Body.Data?.Token);
    }
}
