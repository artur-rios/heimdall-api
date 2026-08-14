using ArturRios.Heimdall.Command.Handlers;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Input.Validation;
using ArturRios.Heimdall.Command.Services;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Mock;
using Moq;

namespace ArturRios.Heimdall.Command.Tests;

// Unit tests for EnableTwoFactorAuthCommandHandler (UC-36): the main flow for each method
// combination, AF-36a (already active), AF-36b (caller not resolvable as an eligible person — the
// stand-in for "caller is a Google User", since a Google-issued token never names a Person to begin
// with), AF-36c (no method selected, via the real validator), and AF-36d (re-initiating over a
// pending setup overwrites it rather than creating a second row).
public class EnableTwoFactorAuthCommandHandlerTests
{
    private static readonly byte[] EncryptedSecret = [1, 2, 3, 4];

    private sealed record Fixture(
        AsyncFakeRepository<Person> Persons,
        AsyncFakeRepository<TwoFactorAuth> TwoFactorAuths,
        AsyncFakeRepository<TwoFactorEmailCode> EmailCodes,
        Mock<ITotpSecretProtector> Protector,
        Mock<ITwoFactorEmailSender> EmailSender,
        Person Person)
    {
        public EnableTwoFactorAuthCommandHandler Handler() =>
            new(
                new EnableTwoFactorAuthCommandValidator(),
                Persons,
                TwoFactorAuths,
                TwoFactorAuths,
                EmailCodes,
                EmailCodes,
                Protector.Object,
                EmailSender.Object);

        public EnableTwoFactorAuthCommand Command(params string[] methods) => new()
        {
            Methods = methods.ToList(), ActingPersonId = Person.PublicId, ActingRole = (int)Roles.User
        };
    }

    private static async Task<Fixture> FixtureAsync(string email = "person@test.local")
    {
        var persons = new AsyncFakeRepository<Person>();
        var person = new Person
        {
            PublicId = Guid.NewGuid(),
            Name = "person",
            Email = email,
            RoleId = (long)Roles.User
        };
        await persons.CreateAsync(person);

        var protector = new Mock<ITotpSecretProtector>();
        protector.Setup(p => p.Protect(It.IsAny<string>())).Returns(EncryptedSecret);

        return new Fixture(
            persons,
            new AsyncFakeRepository<TwoFactorAuth>(),
            new AsyncFakeRepository<TwoFactorEmailCode>(),
            protector,
            new Mock<ITwoFactorEmailSender>(),
            person);
    }

    [UnitFact]
    public async Task GivenEmailWithUriReservedCharacters_WhenHandlingEnableTwoFactorAuth_ThenOtpAuthUriEncodesThem()
    {
        // Given a perfectly ordinary address that is not a URI-safe string. A sub-address like this
        // is the common case: interpolated raw, the '+' is what an authenticator decodes as a space,
        // so the account gets provisioned under a name that is not the caller's.
        var fixture = await FixtureAsync("bob+heimdall@example.com");

        // When
        var output = await fixture.Handler().HandleAsync(fixture.Command("App"));

        // Then — the label is encoded, and the URI still parses as one
        Assert.True(output.Success);
        Assert.Contains("otpauth://totp/Heimdall:bob%2Bheimdall%40example.com", output.Data!.OtpAuthUri);
        Assert.DoesNotContain("bob+heimdall@example.com", output.Data.OtpAuthUri);

        var uri = new Uri(output.Data.OtpAuthUri!);
        Assert.Equal("otpauth", uri.Scheme);
        Assert.Contains("issuer=Heimdall", uri.Query);
    }

    [UnitFact]
    public async Task GivenAppAndEmailSelected_WhenHandlingEnableTwoFactorAuth_ThenPendingRowIsCreatedForBoth()
    {
        // Given a person with no prior configuration
        var fixture = await FixtureAsync();

        // When
        var output = await fixture.Handler().HandleAsync(fixture.Command("App", "Email"));

        // Then — response
        Assert.True(output.Success);
        Assert.Contains(TwoFactorMessages.SetupInitiated, output.Messages);
        Assert.NotNull(output.Data!.OtpAuthUri);
        // The label is percent-encoded, so the '@' arrives as %40 — an address is not a URI-safe
        // string, and one carrying '+', '?' or '#' would otherwise produce a URI an authenticator
        // reads as a different account or truncates outright.
        Assert.Contains("otpauth://totp/Heimdall:person%40test.local", output.Data.OtpAuthUri);
        Assert.True(output.Data.EmailCodeSent);

        // Then — persisted state: exactly one pending (inactive) row, both methods enabled
        var stored = Assert.Single(fixture.TwoFactorAuths.Query().ToList());
        Assert.Equal(fixture.Person.Id, stored.PersonId);
        Assert.False(stored.IsActive);
        Assert.True(stored.AppEnabled);
        Assert.True(stored.EmailEnabled);
        Assert.Equal(EncryptedSecret, stored.TotpSecretEncrypted);

        fixture.EmailSender.Verify(
            sender => sender.SendAsync(fixture.Person.Email, It.IsAny<string>()), Times.Once);
    }

    [UnitFact]
    public async Task GivenOnlyAppSelected_WhenHandlingEnableTwoFactorAuth_ThenOnlyAppIsEnabled()
    {
        // Given
        var fixture = await FixtureAsync();

        // When
        var output = await fixture.Handler().HandleAsync(fixture.Command("App"));

        // Then
        Assert.True(output.Success);
        Assert.NotNull(output.Data!.OtpAuthUri);
        Assert.Null(output.Data.EmailCodeSent);

        var stored = Assert.Single(fixture.TwoFactorAuths.Query().ToList());
        Assert.True(stored.AppEnabled);
        Assert.False(stored.EmailEnabled);

        fixture.EmailSender.Verify(
            sender => sender.SendAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [UnitFact]
    public async Task GivenOnlyEmailSelected_WhenHandlingEnableTwoFactorAuth_ThenOnlyEmailIsEnabled()
    {
        // Given
        var fixture = await FixtureAsync();

        // When
        var output = await fixture.Handler().HandleAsync(fixture.Command("Email"));

        // Then
        Assert.True(output.Success);
        Assert.Null(output.Data!.OtpAuthUri);
        Assert.True(output.Data.EmailCodeSent);

        var stored = Assert.Single(fixture.TwoFactorAuths.Query().ToList());
        Assert.False(stored.AppEnabled);
        Assert.True(stored.EmailEnabled);

        var emailCode = Assert.Single(fixture.EmailCodes.Query().ToList());
        Assert.Equal(stored.Id, emailCode.TwoFactorAuthId);
        Assert.False(emailCode.Used);
    }

    [UnitFact]
    public async Task GivenAlreadyActiveConfiguration_WhenHandlingEnableTwoFactorAuth_ThenReturnsAlreadyActiveError()
    {
        // Given — AF-36a
        var fixture = await FixtureAsync();
        await fixture.TwoFactorAuths.CreateAsync(new TwoFactorAuth
        {
            PersonId = fixture.Person.Id, IsActive = true, AppEnabled = true
        });

        // When
        var output = await fixture.Handler().HandleAsync(fixture.Command("Email"));

        // Then
        Assert.False(output.Success);
        Assert.Contains(TwoFactorMessages.AlreadyActive, output.Errors);
        fixture.EmailSender.Verify(
            sender => sender.SendAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [UnitFact]
    public async Task GivenCallerNamesNoEligiblePerson_WhenHandlingEnableTwoFactorAuth_ThenReturnsNotEligibleError()
    {
        // Given — AF-36b: the acting id resolves to no live Person. This is the shape of both a
        // caller who was hard deleted and a caller who is in fact a Google User, since a
        // Google-issued token never names a row in the Person table (UC-25 step 8).
        var fixture = await FixtureAsync();

        // When
        var output = await fixture.Handler().HandleAsync(new EnableTwoFactorAuthCommand
        {
            Methods = ["App"], ActingPersonId = Guid.NewGuid(), ActingRole = (int)Roles.User
        });

        // Then
        Assert.False(output.Success);
        Assert.Contains(TwoFactorMessages.NotEligible, output.Errors);
        Assert.Empty(fixture.TwoFactorAuths.Query().ToList());
    }

    [UnitFact]
    public async Task GivenNoMethodSelected_WhenHandlingEnableTwoFactorAuth_ThenReturnsNoMethodSelectedError()
    {
        // Given — AF-36c
        var fixture = await FixtureAsync();

        // When
        var output = await fixture.Handler().HandleAsync(fixture.Command());

        // Then
        Assert.False(output.Success);
        Assert.Contains(TwoFactorMessages.NoMethodSelected, output.Errors);
        Assert.Empty(fixture.TwoFactorAuths.Query().ToList());
    }

    [UnitFact]
    public async Task GivenUnknownMethodValue_WhenHandlingEnableTwoFactorAuth_ThenReturnsNoMethodSelectedError()
    {
        // Given — AF-36c's boundary: a method that is neither "App" nor "Email"
        var fixture = await FixtureAsync();

        // When
        var output = await fixture.Handler().HandleAsync(fixture.Command("Sms"));

        // Then
        Assert.False(output.Success);
        Assert.Contains(TwoFactorMessages.NoMethodSelected, output.Errors);
    }

    [UnitFact]
    public async Task GivenPendingSetupExists_WhenHandlingEnableTwoFactorAuthAgain_ThenExistingRowIsOverwritten()
    {
        // Given — AF-36d: a prior, unconfirmed setup for the App method only
        var fixture = await FixtureAsync();
        var pending = new TwoFactorAuth
        {
            PersonId = fixture.Person.Id,
            IsActive = false,
            AppEnabled = true,
            TotpSecretEncrypted = [9, 9, 9]
        };
        await fixture.TwoFactorAuths.CreateAsync(pending);

        // When re-initiating with Email added
        var output = await fixture.Handler().HandleAsync(fixture.Command("Email"));

        // Then — success, and the same row was updated rather than a second one created
        Assert.True(output.Success);
        Assert.Contains(TwoFactorMessages.SetupInitiated, output.Messages);

        var stored = Assert.Single(fixture.TwoFactorAuths.Query().ToList());
        Assert.Equal(pending.Id, stored.Id);
        Assert.True(stored.EmailEnabled);
        Assert.False(stored.IsActive);

        // Then — the App method dropped from this re-initiation is cleared, not left active
        // alongside Email, so UC-37 will only ask for the email code.
        Assert.False(stored.AppEnabled);
        Assert.Null(stored.TotpSecretEncrypted);
    }

    [UnitFact]
    public async Task GivenPendingEmailSetupExists_WhenReinitiatingWithAppOnly_ThenEmailIsCleared()
    {
        // Given — AF-36d: a prior, unconfirmed setup for the Email method only, with a live code
        var fixture = await FixtureAsync();
        var pending = new TwoFactorAuth
        {
            PersonId = fixture.Person.Id, IsActive = false, EmailEnabled = true
        };
        await fixture.TwoFactorAuths.CreateAsync(pending);
        var outstanding = new TwoFactorEmailCode
        {
            TwoFactorAuthId = pending.Id,
            CodeHash = [1],
            Salt = [2],
            ExpiresAt = DateTime.UtcNow.AddMinutes(5),
            Used = false
        };
        await fixture.EmailCodes.CreateAsync(outstanding);

        // When re-initiating with App only
        var output = await fixture.Handler().HandleAsync(fixture.Command("App"));

        // Then — Email is dropped from the pending configuration and its live code retired, so
        // UC-37 will only ask for the app code.
        Assert.True(output.Success);
        var stored = Assert.Single(fixture.TwoFactorAuths.Query().ToList());
        Assert.Equal(pending.Id, stored.Id);
        Assert.True(stored.AppEnabled);
        Assert.False(stored.EmailEnabled);
        Assert.True(outstanding.Used);
    }

    [UnitFact]
    public async Task GivenOutstandingEmailCode_WhenReinitiatingEmailSetup_ThenPriorCodeIsRetired()
    {
        // Given — AF-36d resending the email code: only the freshest one should ever confirm
        var fixture = await FixtureAsync();
        var pending = new TwoFactorAuth
        {
            PersonId = fixture.Person.Id, IsActive = false, EmailEnabled = true
        };
        await fixture.TwoFactorAuths.CreateAsync(pending);
        var outstanding = new TwoFactorEmailCode
        {
            TwoFactorAuthId = pending.Id,
            CodeHash = [1],
            Salt = [2],
            ExpiresAt = DateTime.UtcNow.AddMinutes(5),
            Used = false
        };
        await fixture.EmailCodes.CreateAsync(outstanding);

        // When
        var output = await fixture.Handler().HandleAsync(fixture.Command("Email"));

        // Then
        Assert.True(output.Success);
        Assert.True(outstanding.Used);
        Assert.Equal(2, fixture.EmailCodes.Query().ToList().Count);
    }
}
