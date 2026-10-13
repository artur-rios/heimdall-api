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

// Functional tests for POST /api/auth/register (UC-47). The response is the same for a free and a taken
// address by design, so each test also opens the database: a new person row and a verification token are
// the main flow; their absence is AF-47b. The rows are written after the response, by
// RegistrationDispatcher, so every database assertion first waits for the queue to settle.
[Collection(nameof(FunctionalCollection))]
public class AuthControllerRegisterTests(PostgresFixture db) : WebApiTest<Program>(EnvironmentType.Local)
{
    private const string Password = "Str0ng-Register-Pass!";

    private static string UniqueEmail(string prefix) => $"{prefix}-{Guid.NewGuid():N}@test.local";

    private async Task<Scope> SeedScopeAsync(bool open = true, bool isDeleted = false)
    {
        await using var context = db.CreateContext();
        var scope = new Scope
        {
            PublicId = Guid.NewGuid(), Name = $"scope-{Guid.NewGuid():N}",
            SelfRegistrationEnabled = open, IsDeleted = isDeleted
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

    private Task<HttpOutput<DataOutput<RegisterCommandOutput?>?>> RegisterAsync(
        string email, Guid? scopeId, string password = Password, string name = "New Person") =>
        Gateway.PostAsync<DataOutput<RegisterCommandOutput?>>(
            "/api/auth/register",
            new RegisterCommand { Name = name, Email = email, Password = password, ScopeId = scopeId });

    private async Task<List<Person>> PersonsWithEmailAsync(Scope scope, string email)
    {
        await using var context = db.CreateContext();

        return await context.Persons
            .Where(person => person.ScopeId == scope.Id && person.Email.ToLower() == email.ToLower())
            .ToListAsync();
    }

    private async Task<int> VerificationTokensForAsync(Person person)
    {
        await using var context = db.CreateContext();

        return await context.EmailVerificationTokens.CountAsync(token => token.PersonId == person.Id);
    }

    /// <summary>
    ///     Waits until every registration requested so far has been worked through: the dispatcher takes
    ///     requests one at a time, in order, so once a sentinel registered last exists, every earlier
    ///     request has been processed — including those that rightly created nothing.
    /// </summary>
    private async Task SettleAsync()
    {
        var scope = await SeedScopeAsync();
        var email = UniqueEmail("sentinel");

        await RegisterAsync(email, scope.PublicId);

        var deadline = DateTime.UtcNow.AddSeconds(30);

        while ((await PersonsWithEmailAsync(scope, email)).Count == 0)
        {
            Assert.True(DateTime.UtcNow < deadline, "The registration queue did not settle in time.");
            await Task.Delay(50);
        }
    }

    private static void AssertGenericAcceptance(HttpOutput<DataOutput<RegisterCommandOutput?>?> response)
    {
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Contains(AuthMessages.RegistrationRequested, response.Body!.Messages);
        Assert.Empty(response.Body.Errors);
        Assert.NotNull(response.Body.Data);
    }

    [FunctionalFact]
    public async Task GivenAFreeAddressInAnOpenScope_WhenRegistering_ThenAcceptedAndAnUnverifiedUserExists()
    {
        var scope = await SeedScopeAsync();
        var email = UniqueEmail("new");

        var response = await RegisterAsync(email, scope.PublicId);
        await SettleAsync();

        AssertGenericAcceptance(response);
        var person = Assert.Single(await PersonsWithEmailAsync(scope, email));
        Assert.Equal((long)Roles.User, person.RoleId);
        Assert.False(person.EmailVerified);
        Assert.Equal(1, await VerificationTokensForAsync(person));
    }

    [FunctionalFact]
    public async Task GivenATakenAddress_WhenRegistering_ThenTheSameAnswerAndNoSecondPerson()
    {
        var scope = await SeedScopeAsync();
        var email = UniqueEmail("taken");
        var existing = await SeedUserAsync(scope, email);

        var response = await RegisterAsync(email.ToUpperInvariant(), scope.PublicId);
        await SettleAsync();

        AssertGenericAcceptance(response);
        Assert.Single(await PersonsWithEmailAsync(scope, email));
        Assert.Equal(0, await VerificationTokensForAsync(existing));
    }

    [FunctionalFact]
    public async Task GivenTheSameNewAddressTwice_WhenRegistering_ThenOnePersonIsCreated()
    {
        var scope = await SeedScopeAsync();
        var email = UniqueEmail("twice");

        var first = await RegisterAsync(email, scope.PublicId);
        var second = await RegisterAsync(email, scope.PublicId);
        await SettleAsync();

        AssertGenericAcceptance(first);
        AssertGenericAcceptance(second);
        Assert.Single(await PersonsWithEmailAsync(scope, email));
    }

    [FunctionalFact]
    public async Task GivenAClosedScope_WhenRegistering_ThenForbiddenWithTheUnavailableMessage()
    {
        var scope = await SeedScopeAsync(open: false);

        var response = await RegisterAsync(UniqueEmail("closed"), scope.PublicId);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains(AuthMessages.RegistrationUnavailable, response.Body!.Errors);
    }

    [FunctionalFact]
    public async Task GivenAnUnknownScope_WhenRegistering_ThenForbiddenWithTheUnavailableMessage()
    {
        var response = await RegisterAsync(UniqueEmail("nowhere"), Guid.NewGuid());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains(AuthMessages.RegistrationUnavailable, response.Body!.Errors);
    }

    [FunctionalFact]
    public async Task GivenAShortPassword_WhenRegistering_ThenBadRequestAndNothingIsCreated()
    {
        var scope = await SeedScopeAsync();
        var email = UniqueEmail("short");

        var response = await RegisterAsync(email, scope.PublicId, password: "1234567");
        await SettleAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(PersonMessages.PasswordTooShort, response.Body!.Errors);
        Assert.Empty(await PersonsWithEmailAsync(scope, email));
    }

    [FunctionalFact]
    public async Task GivenAnyRequest_WhenRegistering_ThenThePasswordIsNotEchoed()
    {
        var scope = await SeedScopeAsync();

        var response = await RegisterAsync(UniqueEmail("echo"), scope.PublicId);

        Assert.DoesNotContain(Password, System.Text.Json.JsonSerializer.Serialize(response.Body));
    }
}
