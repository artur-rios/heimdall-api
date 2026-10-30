using ArturRios.Heimdall.Command.Services;
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
    private const string Password = Seeds.Password;

    private static string UniqueEmail(string prefix) => $"{prefix}-{Guid.NewGuid():N}@test.local";

    /// <summary>
    ///     An active configuration holding one recovery code, which the test returns: the App method, or
    ///     the Email method, which an own email change refuses (FR-PE-18).
    /// </summary>
    private async Task<string> SeedActiveTwoFactorAsync(Person person, bool emailMethod = false)
    {
        await using var context = db.CreateContext();

        var twoFactorAuth = new TwoFactorAuth
        {
            PersonId = person.Id, IsActive = true, AppEnabled = !emailMethod, EmailEnabled = emailMethod,
            TotpSecretEncrypted = emailMethod ? null : [1, 2, 3]
        };
        context.TwoFactorAuths.Add(twoFactorAuth);
        await context.SaveChangesAsync();

        var recoveryCode = $"TEST-{Guid.NewGuid():N}"[..9].ToUpperInvariant();
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
        var person = await Seeds.PersonAsync(db, Roles.ScopeAdmin, new PersonOptions { EmailPrefix = "reauth-none" });
        ActAs(person);

        // When
        var response = await UpdateAsync(person, person.Name, UniqueEmail("attacker"));

        // Then
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(AuthMessages.PasswordOrCodeIncorrect, response.Body!.Errors);
        await AssertUnchangedAsync(person);
    }

    [FunctionalFact]
    public async Task GivenOwnEmailChangeWithAWrongPassword_WhenPutPerson_ThenUnauthorizedAndNothingChanges()
    {
        // Given
        var person = await Seeds.PersonAsync(db, Roles.ScopeAdmin, new PersonOptions { EmailPrefix = "reauth-wrong" });
        ActAs(person);

        // When
        var response = await UpdateAsync(person, person.Name, UniqueEmail("attacker"), "not-the-password");

        // Then
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(AuthMessages.PasswordOrCodeIncorrect, response.Body!.Errors);
        await AssertUnchangedAsync(person);
    }

    [FunctionalFact]
    public async Task GivenOwnEmailChangeWithThePasswordButNoCode_WhenTwoFactorIsActive_ThenUnauthorizedAndNothingChanges()
    {
        // Given
        var person = await Seeds.PersonAsync(db, Roles.ScopeAdmin, new PersonOptions { EmailPrefix = "reauth-no-code" });
        await SeedActiveTwoFactorAsync(person);
        ActAs(person);

        // When
        var response = await UpdateAsync(person, person.Name, UniqueEmail("attacker"), Password);

        // Then
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(AuthMessages.PasswordOrCodeIncorrect, response.Body!.Errors);
        await AssertUnchangedAsync(person);
    }

    [FunctionalFact]
    public async Task GivenOwnEmailChangeWithThePasswordAndACode_WhenTwoFactorIsActive_ThenUpdatedAndTheTokenMustBeRenewed()
    {
        // Given
        var person = await Seeds.PersonAsync(db, Roles.ScopeAdmin, new PersonOptions { EmailPrefix = "reauth-code" });
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
        var person = await Seeds.PersonAsync(db, Roles.ScopeAdmin, new PersonOptions { EmailPrefix = "reauth-password" });
        ActAs(person);
        var newEmail = UniqueEmail("reauth-password-new");

        // When
        var response = await UpdateAsync(person, person.Name, newEmail, Password);

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(newEmail, (await ReloadAsync(person)).Email);
    }

    [FunctionalFact]
    public async Task GivenALiveResetLink_WhenTheOwnEmailChanges_ThenTheLinkNoLongerSetsAPassword()
    {
        // Given a reset link mailed to the current address (FR-PE-17), of a person who could use it
        var person = await Seeds.PersonAsync(db, Roles.SystemAdmin, new PersonOptions { EmailPrefix = "reset-link" });
        var plaintext = $"token-{Guid.NewGuid():N}";

        await using (var context = db.CreateContext())
        {
            context.PasswordResetTokens.Add(new PasswordResetToken
            {
                PersonId = person.Id,
                TokenHash = ArturRios.Heimdall.Command.Services.SingleUseTokenHash.Of(plaintext),
                ExpiresAt = DateTime.UtcNow.AddHours(1)
            });
            await context.SaveChangesAsync();
        }

        ActAs(person);
        var changed = await UpdateAsync(person, person.Name, UniqueEmail("reset-link-new"), Password);
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);

        // When the link is followed afterwards
        var reset = await Gateway.PostAsync<DataOutput<ResetPasswordCommandOutput?>>(
            "/api/auth/password-reset",
            new ResetPasswordCommand { Token = plaintext, NewPassword = "An0ther-Str0ng-Pass!" });

        // Then — the link was retired with the address, and the password is the one it was
        Assert.Equal(HttpStatusCode.BadRequest, reset.StatusCode);
        Assert.Contains(AuthMessages.TokenAlreadyUsed, reset.Body!.Errors);
        var stored = await ReloadAsync(person);
        Assert.True(Hash.TextMatches(Password, stored.PasswordHash, stored.Salt, PasswordHashing.Configuration));
    }

    [FunctionalFact]
    public async Task GivenSystemAdminChangingAnotherPersonsEmailWithoutAPassword_WhenPutPerson_ThenUpdated()
    {
        // Given — the administrator's own authorisation covers it, as before
        var admin = await Seeds.PersonAsync(db, Roles.SystemAdmin, new PersonOptions { EmailPrefix = "reauth-admin" });
        var target = await Seeds.PersonAsync(db, Roles.ScopeAdmin, new PersonOptions { EmailPrefix = "reauth-target" });
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
        var person = await Seeds.PersonAsync(db, Roles.ScopeAdmin, new PersonOptions { EmailPrefix = "reauth-name" });
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
        var admin = await Seeds.PersonAsync(db, Roles.SystemAdmin, new PersonOptions { EmailPrefix = "reauth-own-admin" });
        ActAs(admin);

        // When
        var response = await UpdateAsync(admin, admin.Name, UniqueEmail("reauth-own-admin-new"));

        // Then
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(AuthMessages.PasswordOrCodeIncorrect, response.Body!.Errors);
        await AssertUnchangedAsync(admin);
    }

    [FunctionalFact]
    public async Task GivenSystemAdminChangingTheirOwnEmailWithPassword_WhenPutPerson_ThenUpdatedAndTheirTokensRevoked()
    {
        // Given
        var admin = await Seeds.PersonAsync(db, Roles.SystemAdmin, new PersonOptions { EmailPrefix = "reauth-own-admin-ok" });
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

    [FunctionalFact]
    public async Task GivenOwnAddressPaddedWithWhitespace_WhenPutPersonWithoutPassword_ThenNothingNewIsStored()
    {
        // Given — the same address with surrounding whitespace is no change, and is not written
        var person = await Seeds.PersonAsync(db, Roles.ScopeAdmin, new PersonOptions { EmailPrefix = "reauth-padded" });
        ActAs(person);

        // When
        var response = await UpdateAsync(person, person.Name, $"  {person.Email} ");

        // Then — accepted without a password, and the stored address is exactly what it was, so
        // sign-in still finds it and the token is not revoked
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await AssertUnchangedAsync(person);

        var next = await Gateway.GetAsync<DataOutput<object?>>($"/api/persons/{person.PublicId}");
        Assert.Equal(HttpStatusCode.OK, next.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenTheEmailSignInMethodIsActive_WhenChangingOwnEmailWithThePasswordAndACode_ThenConflictAndNothingChanges()
    {
        // Given — FR-PE-18 (AF-08k): sign-in codes would go to an address nobody has proved
        var person = await Seeds.PersonAsync(db, Roles.ScopeAdmin, new PersonOptions { EmailPrefix = "reauth-email-method" });
        var recoveryCode = await SeedActiveTwoFactorAsync(person, emailMethod: true);
        ActAs(person);

        // When
        var response = await UpdateAsync(person, person.Name, UniqueEmail("elsewhere"), Password, recoveryCode);

        // Then — refused, nothing changed, and the recovery code is still good
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains(PersonMessages.EmailSignInMethodEnabled, response.Body!.Errors);
        await AssertUnchangedAsync(person);

        await using var context = db.CreateContext();
        Assert.False(await context.TwoFactorRecoveryCodes
            .Where(code => code.TwoFactorAuth.PersonId == person.Id)
            .AnyAsync(code => code.UsedAt != null));
    }
}
