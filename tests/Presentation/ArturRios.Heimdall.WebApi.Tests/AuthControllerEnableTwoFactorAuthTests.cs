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

// Functional tests for POST /api/auth/2fa/enable (UC-36, FR-2F-01…FR-2F-03): the main flow for each
// method combination, AF-36a (409, already active), AF-36b (403 — no bearer token names an eligible
// person, standing in for a Google User), AF-36c (400, no method selected), AF-36d (re-initiating
// over a pending setup overwrites it), and the 401 an unauthenticated caller gets.
[Collection(nameof(FunctionalCollection))]
public class AuthControllerEnableTwoFactorAuthTests(PostgresFixture db)
    : WebApiTest<Program>(EnvironmentType.Local)
{
    private const string Password = "Str0ng-2fa-Pass!";

    private static string UniqueEmail(string prefix) => $"{prefix}-{Guid.NewGuid():N}@test.local";

    private async Task<Person> SeedPersonAsync(Roles role, string email, bool isDeleted = false)
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
            EmailVerified = true,
            IsDeleted = isDeleted
        };
        context.Persons.Add(person);
        await context.SaveChangesAsync();
        return person;
    }

    private async Task<TwoFactorAuth?> ConfigurationForAsync(Person person)
    {
        await using var context = db.CreateContext();

        return await context.TwoFactorAuths
            .Include(x => x.EmailCodes)
            .FirstOrDefaultAsync(x => x.PersonId == person.Id);
    }

    private Task<HttpOutput<DataOutput<EnableTwoFactorAuthCommandOutput?>?>> EnableAsync(
        params string[] methods) =>
        Gateway.PostAsync<DataOutput<EnableTwoFactorAuthCommandOutput?>>(
            "/api/auth/2fa/enable", new EnableTwoFactorAuthCommand { Methods = methods.ToList() });

    [FunctionalFact]
    public async Task GivenAppAndEmailSelected_WhenPostEnable2fa_ThenPendingConfigurationIsCreatedForBoth()
    {
        // Given an authenticated person with no prior two-factor configuration
        var person = await SeedPersonAsync(Roles.SystemAdmin, UniqueEmail("admin"));
        Authorize(TestTokens.For(person.PublicId, (int)Roles.SystemAdmin));

        // When
        var response = await EnableAsync("App", "Email");

        // Then — response
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(TwoFactorMessages.SetupInitiated, response.Body!.Messages);
        Assert.NotNull(response.Body.Data?.OtpAuthUri);
        Assert.StartsWith("otpauth://totp/Heimdall:", response.Body.Data!.OtpAuthUri);
        Assert.True(response.Body.Data.EmailCodeSent);

        // Then — database state: a pending row, inactive, both methods on, the secret encrypted
        var stored = await ConfigurationForAsync(person);
        Assert.NotNull(stored);
        Assert.False(stored!.IsActive);
        Assert.True(stored.AppEnabled);
        Assert.True(stored.EmailEnabled);
        Assert.NotNull(stored.TotpSecretEncrypted);
        Assert.NotEmpty(stored.TotpSecretEncrypted!);
        Assert.Single(stored.EmailCodes);
    }

    [FunctionalFact]
    public async Task GivenOnlyAppSelected_WhenPostEnable2fa_ThenOnlyAppIsConfigured()
    {
        // Given
        var person = await SeedPersonAsync(Roles.User, UniqueEmail("user"));
        Authorize(TestTokens.For(person.PublicId, (int)Roles.User));

        // When
        var response = await EnableAsync("App");

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(response.Body!.Data!.OtpAuthUri);
        Assert.Null(response.Body.Data.EmailCodeSent);

        var stored = await ConfigurationForAsync(person);
        Assert.True(stored!.AppEnabled);
        Assert.False(stored.EmailEnabled);
        Assert.Empty(stored.EmailCodes);
    }

    [FunctionalFact]
    public async Task GivenOnlyEmailSelected_WhenPostEnable2fa_ThenOnlyEmailIsConfigured()
    {
        // Given
        var person = await SeedPersonAsync(Roles.ScopeAdmin, UniqueEmail("scope-admin"));
        Authorize(TestTokens.For(person.PublicId, (int)Roles.ScopeAdmin));

        // When
        var response = await EnableAsync("Email");

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(response.Body!.Data!.OtpAuthUri);
        Assert.True(response.Body.Data.EmailCodeSent);

        var stored = await ConfigurationForAsync(person);
        Assert.False(stored!.AppEnabled);
        Assert.True(stored.EmailEnabled);
        Assert.Single(stored.EmailCodes);
    }

    [FunctionalFact]
    public async Task GivenAlreadyActiveConfiguration_WhenPostEnable2fa_ThenConflict()
    {
        // Given — AF-36a
        var person = await SeedPersonAsync(Roles.SystemAdmin, UniqueEmail("admin"));
        await using (var context = db.CreateContext())
        {
            context.TwoFactorAuths.Add(new TwoFactorAuth
            {
                PersonId = person.Id, IsActive = true, AppEnabled = true
            });
            await context.SaveChangesAsync();
        }
        Authorize(TestTokens.For(person.PublicId, (int)Roles.SystemAdmin));

        // When
        var response = await EnableAsync("Email");

        // Then
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains(TwoFactorMessages.AlreadyActive, response.Body!.Errors);
    }

    [FunctionalFact]
    public async Task GivenNoMethodSelected_WhenPostEnable2fa_ThenBadRequest()
    {
        // Given — AF-36c
        var person = await SeedPersonAsync(Roles.SystemAdmin, UniqueEmail("admin"));
        Authorize(TestTokens.For(person.PublicId, (int)Roles.SystemAdmin));

        // When
        var response = await EnableAsync();

        // Then
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(TwoFactorMessages.NoMethodSelected, response.Body!.Errors);
        Assert.Null(await ConfigurationForAsync(person));
    }

    [FunctionalFact]
    public async Task GivenPendingSetupExists_WhenPostEnable2faAgain_ThenExistingRowIsOverwritten()
    {
        // Given — AF-36d: a prior, unconfirmed App-only setup
        var person = await SeedPersonAsync(Roles.SystemAdmin, UniqueEmail("admin"));
        Authorize(TestTokens.For(person.PublicId, (int)Roles.SystemAdmin));

        var first = await EnableAsync("App");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var firstUri = first.Body!.Data!.OtpAuthUri;

        // When re-initiating with Email added
        var second = await EnableAsync("App", "Email");

        // Then — success, still one row, both methods now on, a regenerated secret
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.NotEqual(firstUri, second.Body!.Data!.OtpAuthUri);

        await using var context = db.CreateContext();
        var rows = await context.TwoFactorAuths.Where(x => x.PersonId == person.Id).ToListAsync();
        var stored = Assert.Single(rows);
        Assert.True(stored.AppEnabled);
        Assert.True(stored.EmailEnabled);
        Assert.False(stored.IsActive);
    }

    [FunctionalFact]
    public async Task GivenNoBearerToken_WhenPostEnable2fa_ThenUnauthorized()
    {
        // Given no bearer token on the gateway
        // When
        var response = await EnableAsync("App");

        // Then
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenTokenNamingNoEligiblePerson_WhenPostEnable2fa_ThenForbidden()
    {
        // Given — AF-36b: a bearer token naming no row in the Person table at all, the same shape a
        // Google-issued token has here, since a Google User is never a Person (UC-25 step 8) and so
        // can never be resolved by this lookup either.
        Authorize(TestTokens.ForRole((int)Roles.User));

        // When
        var response = await EnableAsync("App");

        // Then
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains(TwoFactorMessages.NotEligible, response.Body!.Errors);
    }

    [FunctionalFact]
    public async Task GivenLogicallyDeletedPerson_WhenPostEnable2fa_ThenForbidden()
    {
        // Given — the same AF-36b refusal for a caller whose account was since logically deleted
        var person = await SeedPersonAsync(Roles.SystemAdmin, UniqueEmail("deleted"), isDeleted: true);
        Authorize(TestTokens.For(person.PublicId, (int)Roles.SystemAdmin));

        // When
        var response = await EnableAsync("App");

        // Then
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains(TwoFactorMessages.NotEligible, response.Body!.Errors);
    }
}
