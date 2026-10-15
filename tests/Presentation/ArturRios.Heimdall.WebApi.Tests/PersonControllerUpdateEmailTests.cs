using System.Net;
using System.Security.Cryptography;
using System.Text;
using ArturRios.Configuration.Enums;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Heimdall.WebApi.Security;
using ArturRios.Heimdall.WebApi.Tests.Support;
using ArturRios.Output;
using ArturRios.Util.Hashing;
using ArturRios.Util.Http;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Functional;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Heimdall.WebApi.Tests;

// Functional tests for the re-authentication an own email change needs (UC-08 step 4, FR-PE-14),
// over PUT /api/persons/{id}. An address change requires proof of identity, because recovery follows
// the address: the person's current password is asked for, and their second factor when it is active. An administrator changing someone else's address, and a person changing
// only their name, are asked for nothing. Every refusal leaves the stored address where it was.
[Collection(nameof(FunctionalCollection))]
public class PersonControllerUpdateEmailTests(PostgresFixture db) : WebApiTest<Program>(EnvironmentType.Local)
{
    private const string Password = "Str0ng-Email-Change-Pass!";

    private static string UniqueEmail(string prefix) => $"{prefix}-{Guid.NewGuid():N}@test.local";

    private async Task<Person> SeedPersonAsync(string prefix, Roles role = Roles.ScopeAdmin)
    {
        await using var context = db.CreateContext();

        var person = new Person
        {
            PublicId = Guid.NewGuid(),
            Name = "Email Change",
            Email = UniqueEmail(prefix),
            PasswordHash = Hash.EncodeWithRandomSalt(Password, out var salt),
            Salt = salt,
            RoleId = (long)role,
            EmailVerified = true
        };

        context.Persons.Add(person);
        await context.SaveChangesAsync();

        return person;
    }

    /// <summary>An active Email configuration holding one recovery code, which the test returns.</summary>
    private async Task<string> SeedActiveTwoFactorAsync(Person person)
    {
        await using var context = db.CreateContext();

        var twoFactorAuth = new TwoFactorAuth
        {
            PersonId = person.Id, IsActive = true, AppEnabled = false, EmailEnabled = true
        };
        context.TwoFactorAuths.Add(twoFactorAuth);
        await context.SaveChangesAsync();

        var recoveryCode = $"TEST-{Guid.NewGuid():N}"[..9];
        context.TwoFactorRecoveryCodes.Add(new TwoFactorRecoveryCode
        {
            TwoFactorAuthId = twoFactorAuth.Id, CodeHash = SHA256.HashData(Encoding.UTF8.GetBytes(recoveryCode))
        });
        await context.SaveChangesAsync();

        return recoveryCode;
    }

    private async Task<Person> ReloadAsync(Person person)
    {
        await using var context = db.CreateContext();

        return await context.Persons.SingleAsync(x => x.Id == person.Id);
    }

    private void ActAs(Person person) =>
        Authorize(TestTokens.For(person.PublicId, (int)person.RoleId));

    private Task<HttpOutput<DataOutput<UpdatePersonCommandOutput?>?>> UpdateAsync(
        Person person, string name, string email, string? currentPassword = null, string? twoFactorCode = null) =>
        Gateway.PutAsync<DataOutput<UpdatePersonCommandOutput?>>(
            $"/api/persons/{person.PublicId}",
            new UpdatePersonCommand
            {
                Name = name, Email = email, CurrentPassword = currentPassword, TwoFactorCode = twoFactorCode
            });

    private async Task AssertUnchangedAsync(Person person)
    {
        var stored = await ReloadAsync(person);

        Assert.Equal(person.Email, stored.Email);
        Assert.True(stored.EmailVerified);
        Assert.Equal(person.SecurityStamp, stored.SecurityStamp);
    }

    [FunctionalFact]
    public async Task GivenOwnEmailChangeWithoutPassword_WhenPutPerson_ThenUnauthorizedAndNothingChanges()
    {
        // Given — the bearer token is all the caller presents
        var person = await SeedPersonAsync("reauth-none");
        ActAs(person);

        // When
        var response = await UpdateAsync(person, person.Name, UniqueEmail("attacker"));

        // Then
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(TwoFactorMessages.PasswordMismatch, response.Body!.Errors);
        await AssertUnchangedAsync(person);
    }

    [FunctionalFact]
    public async Task GivenOwnEmailChangeWithAWrongPassword_WhenPutPerson_ThenUnauthorizedAndNothingChanges()
    {
        // Given
        var person = await SeedPersonAsync("reauth-wrong");
        ActAs(person);

        // When
        var response = await UpdateAsync(person, person.Name, UniqueEmail("attacker"), "not-the-password");

        // Then
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(TwoFactorMessages.PasswordMismatch, response.Body!.Errors);
        await AssertUnchangedAsync(person);
    }

    [FunctionalFact]
    public async Task GivenOwnEmailChangeWithThePasswordButNoCode_WhenTwoFactorIsActive_ThenUnauthorizedAndNothingChanges()
    {
        // Given
        var person = await SeedPersonAsync("reauth-no-code");
        await SeedActiveTwoFactorAsync(person);
        ActAs(person);

        // When
        var response = await UpdateAsync(person, person.Name, UniqueEmail("attacker"), Password);

        // Then
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(TwoFactorMessages.FactorInvalid, response.Body!.Errors);
        await AssertUnchangedAsync(person);
    }

    [FunctionalFact]
    public async Task GivenOwnEmailChangeWithThePasswordAndACode_WhenTwoFactorIsActive_ThenUpdatedAndTheTokenMustBeRenewed()
    {
        // Given
        var person = await SeedPersonAsync("reauth-code");
        var recoveryCode = await SeedActiveTwoFactorAsync(person);
        ActAs(person);
        var newEmail = UniqueEmail("reauth-code-new");

        // When
        var response = await UpdateAsync(person, person.Name, newEmail, Password, recoveryCode);

        // Then — the address changed and is no longer verified
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(newEmail, response.Body!.Data!.Email);
        Assert.False(response.Body.Data.EmailVerified);

        var stored = await ReloadAsync(person);
        Assert.Equal(newEmail, stored.Email);

        // Then — the token that made the change is revoked with every other (FR-AU-11), so the client
        // signs in again, now with the new address
        var next = await Gateway.GetAsync<DataOutput<object?>>($"/api/persons/{person.PublicId}");
        Assert.Equal(HttpStatusCode.Unauthorized, next.StatusCode);
        Assert.Contains(ActorLivenessFilter.ActorTokenRevoked, next.Body!.Errors);
    }

    [FunctionalFact]
    public async Task GivenOwnEmailChangeWithThePassword_WhenTwoFactorIsNotActive_ThenUpdated()
    {
        // Given
        var person = await SeedPersonAsync("reauth-password");
        ActAs(person);
        var newEmail = UniqueEmail("reauth-password-new");

        // When
        var response = await UpdateAsync(person, person.Name, newEmail, Password);

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(newEmail, (await ReloadAsync(person)).Email);
    }

    [FunctionalFact]
    public async Task GivenSystemAdminChangingAnotherPersonsEmailWithoutAPassword_WhenPutPerson_ThenUpdated()
    {
        // Given — the administrator's own authorisation covers it, as before
        var admin = await SeedPersonAsync("reauth-admin", Roles.SystemAdmin);
        var target = await SeedPersonAsync("reauth-target");
        await SeedActiveTwoFactorAsync(target);
        ActAs(admin);
        var newEmail = UniqueEmail("reauth-target-new");

        // When
        var response = await UpdateAsync(target, target.Name, newEmail);

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(newEmail, (await ReloadAsync(target)).Email);
    }

    [FunctionalFact]
    public async Task GivenSelfNameOnlyChangeWithoutAPassword_WhenPutPerson_ThenUpdatedAndTheTokenStillWorks()
    {
        // Given
        var person = await SeedPersonAsync("reauth-name");
        ActAs(person);

        // When
        var response = await UpdateAsync(person, "Renamed", person.Email);

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Renamed", (await ReloadAsync(person)).Name);

        var next = await Gateway.GetAsync<DataOutput<object?>>($"/api/persons/{person.PublicId}");
        Assert.Equal(HttpStatusCode.OK, next.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenSystemAdminChangingTheirOwnEmailWithoutPassword_WhenPutPerson_ThenUnauthorizedAndNothingChanges()
    {
        // Given — the role grants nothing here: an administrator's own address is theirs to prove
        var admin = await SeedPersonAsync("reauth-own-admin", Roles.SystemAdmin);
        ActAs(admin);

        // When
        var response = await UpdateAsync(admin, admin.Name, UniqueEmail("reauth-own-admin-new"));

        // Then
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(TwoFactorMessages.PasswordMismatch, response.Body!.Errors);
        await AssertUnchangedAsync(admin);
    }

    [FunctionalFact]
    public async Task GivenSystemAdminChangingTheirOwnEmailWithPassword_WhenPutPerson_ThenUpdatedAndTheirTokensRevoked()
    {
        // Given
        var admin = await SeedPersonAsync("reauth-own-admin-ok", Roles.SystemAdmin);
        ActAs(admin);
        var newEmail = UniqueEmail("reauth-own-admin-ok-new");

        // When
        var response = await UpdateAsync(admin, admin.Name, newEmail, Password);

        // Then — changed, the stamp rotated, and the token that did it refused from now on
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = await ReloadAsync(admin);
        Assert.Equal(newEmail, stored.Email);
        Assert.NotEqual(admin.SecurityStamp, stored.SecurityStamp);

        var next = await Gateway.GetAsync<DataOutput<object?>>($"/api/persons/{admin.PublicId}");
        Assert.Equal(HttpStatusCode.Unauthorized, next.StatusCode);
        Assert.Contains(ActorLivenessFilter.ActorTokenRevoked, next.Body!.Errors);
    }
}
