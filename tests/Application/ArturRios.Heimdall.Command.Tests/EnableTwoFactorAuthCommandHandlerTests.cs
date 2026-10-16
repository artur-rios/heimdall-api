using ArturRios.Heimdall.Command.Handlers;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Input.Validation;
using ArturRios.Heimdall.Command.Services;
using ArturRios.Heimdall.Command.Tests.Support;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Util.Hashing;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Mock;
using Moq;

namespace ArturRios.Heimdall.Command.Tests;

// Unit tests for EnableTwoFactorAuthCommandHandler (UC-36): the main flow for each method
// combination, AF-36a (already active), AF-36b (caller not resolvable as an eligible person — the
// stand-in for "caller is a Google User", since a Google-issued token never names a Person to begin
// with), AF-36c (no method selected, via the real validator), and AF-36d (re-initiating over a
// pending setup overwrites it rather than creating a second row). Also the current password the
// request has to carry (FR-2F-18).
public class EnableTwoFactorAuthCommandHandlerTests
{
    private const string Password = "Str0ng-Enable-Pass!";

    private static readonly byte[] EncryptedSecret = [1, 2, 3, 4];

    /// <summary>One Argon2id derivation for the whole class, shared by every person it builds.</summary>
    private static readonly Lazy<(byte[] Hash, byte[] Salt)> Credential = new(() =>
    {
        var hash = Hash.EncodeWithRandomSalt(Password, out var salt);

        return (hash, salt);
    });

    private sealed record Fixture(
        AsyncFakeRepository<Person, long> Persons,
        AsyncFakeRepository<TwoFactorAuth, long> TwoFactorAuths,
        AsyncFakeRepository<TwoFactorEmailCode, long> EmailCodes,
        Mock<ITotpSecretProtector> Protector,
        Mock<ITwoFactorEmailSender> EmailSender,
        Person Person)
    {
        /// <summary>The waits the handler advised, as the <c>Retry-After</c> of its refusals.</summary>
        public RecordingRetryAfterAdvisor RetryAfter { get; } = new();

        public EnableTwoFactorAuthCommandHandler Handler() =>
            new(
                new EnableTwoFactorAuthCommandValidator(),
                Persons,
                TwoFactorAuths,
                TwoFactorAuths,
                // The real issuer over the fake repositories and the recording sender, so the
                // assertions about what lands in EmailCodes — and about the address the code went
                // to — keep testing the behaviour rather than the call.
                new TwoFactorEmailCodeIssuer(EmailCodes, EmailCodes, EmailSender.Object),
                Protector.Object,
                InMemoryAtomicWrites.Instance,
                new AdminEmailOptions { Cooldown = TimeSpan.FromMinutes(5) },
                RetryAfter);

        public EnableTwoFactorAuthCommand Command(params string[] methods) => new()
        {
            Methods = methods.ToList(),
            CurrentPassword = Password,
            ActingPersonId = Person.PublicId,
            ActingRole = (int)Roles.User
        };
    }

    private static async Task<Fixture> FixtureAsync(string email = "person@test.local")
    {
        var persons = new AsyncFakeRepository<Person, long>();
        var person = new Person
        {
            PublicId = Guid.NewGuid(),
            Name = "person",
            Email = email,
            PasswordHash = Credential.Value.Hash,
            Salt = Credential.Value.Salt,
            RoleId = (long)Roles.User
        };
        await persons.CreateAsync(person);

        var protector = new Mock<ITotpSecretProtector>();
        protector.Setup(p => p.Protect(It.IsAny<string>())).Returns(EncryptedSecret);

        return new Fixture(
            persons,
            new AsyncFakeRepository<TwoFactorAuth, long>(),
            new AsyncFakeRepository<TwoFactorEmailCode, long>(),
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
        // Given — AF-36d resending the email code: only the freshest one should ever confirm. The
        // code still lives four minutes, so it was mailed six minutes ago, outside the cooldown.
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
            ExpiresAt = DateTime.UtcNow.AddMinutes(4),
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

    // FR-2F-18: a bearer token alone cannot choose the account's second factor.

    [UnitFact]
    public async Task GivenNoPassword_WhenHandlingEnableTwoFactorAuth_ThenRefusedAndNothingIsCreated()
    {
        // Given
        var fixture = await FixtureAsync();
        var command = fixture.Command("App");
        command.CurrentPassword = null;

        // When
        var output = await fixture.Handler().HandleAsync(command);

        // Then
        Assert.False(output.Success);
        Assert.Contains(AuthMessages.PasswordOrCodeIncorrect, output.Errors);
        Assert.Empty(fixture.TwoFactorAuths.Query().ToList());
    }

    [UnitFact]
    public async Task GivenAWrongPassword_WhenHandlingEnableTwoFactorAuth_ThenRefusedAndNothingIsCreated()
    {
        // Given
        var fixture = await FixtureAsync();
        var command = fixture.Command("App", "Email");
        command.CurrentPassword = "not-the-password";

        // When
        var output = await fixture.Handler().HandleAsync(command);

        // Then
        Assert.False(output.Success);
        Assert.Contains(AuthMessages.PasswordOrCodeIncorrect, output.Errors);
        Assert.Empty(fixture.TwoFactorAuths.Query().ToList());
        Assert.Empty(fixture.EmailCodes.Query().ToList());
    }

    [UnitFact]
    public async Task GivenTheRightPassword_WhenHandlingEnableTwoFactorAuth_ThenSetupIsInitiated()
    {
        // Given
        var fixture = await FixtureAsync();

        // When
        var output = await fixture.Handler().HandleAsync(fixture.Command("App"));

        // Then
        Assert.True(output.Success);
        Assert.Contains(TwoFactorMessages.SetupInitiated, output.Messages);
        Assert.Single(fixture.TwoFactorAuths.Query().ToList());
    }

    // FR-AU-13: the re-authentication budget

    [UnitFact]
    public async Task GivenAPausedBudget_WhenTheRightPasswordIsPresented_ThenRefusedAndNothingIsCreated()
    {
        // Given
        var fixture = await FixtureAsync();
        fixture.Person.ReauthenticationPausedUntil = DateTime.UtcNow.AddMinutes(10);

        // When
        var output = await fixture.Handler().HandleAsync(fixture.Command("App", "Email"));

        // Then — the same answer a wrong password gets
        Assert.Equal([AuthMessages.PasswordOrCodeIncorrect], output.Errors);
        Assert.Empty(fixture.TwoFactorAuths.Query().ToList());
        Assert.Empty(fixture.EmailCodes.Query().ToList());
    }

    [UnitFact]
    public async Task GivenFourFailuresInAnOpenWindow_WhenAnotherPasswordIsWrong_ThenReauthenticationPausesForFifteenMinutes()
    {
        // Given
        var fixture = await FixtureAsync();
        fixture.Person.FailedReauthenticationAttempts = ReauthenticationBudget.MaxFailures - 1;
        fixture.Person.ReauthenticationWindowStartedAt = DateTime.UtcNow.AddMinutes(-14);
        var command = fixture.Command("App");
        command.CurrentPassword = "not-the-password";
        var before = DateTime.UtcNow;

        // When
        await fixture.Handler().HandleAsync(command);

        // Then
        Assert.InRange(fixture.Person.ReauthenticationPausedUntil!.Value,
            before.AddMinutes(15), DateTime.UtcNow.AddMinutes(15));
        Assert.Equal(0, fixture.Person.FailedReauthenticationAttempts);
        Assert.Null(fixture.Person.ReauthenticationWindowStartedAt);
    }

    [UnitFact]
    public async Task GivenFourFailuresInAWindowThatHasRunOut_WhenAnotherPasswordIsWrong_ThenANewWindowStarts()
    {
        // Given
        var fixture = await FixtureAsync();
        fixture.Person.FailedReauthenticationAttempts = ReauthenticationBudget.MaxFailures - 1;
        fixture.Person.ReauthenticationWindowStartedAt = DateTime.UtcNow.AddMinutes(-16);
        var command = fixture.Command("App");
        command.CurrentPassword = "not-the-password";
        var before = DateTime.UtcNow;

        // When
        await fixture.Handler().HandleAsync(command);

        // Then
        Assert.Null(fixture.Person.ReauthenticationPausedUntil);
        Assert.Equal(1, fixture.Person.FailedReauthenticationAttempts);
        Assert.InRange(fixture.Person.ReauthenticationWindowStartedAt!.Value, before, DateTime.UtcNow);
    }

    [UnitFact]
    public async Task GivenAPauseThatHasRunOut_WhenTheRightPasswordIsPresented_ThenSetupIsInitiatedAndThePauseIsCleared()
    {
        // Given
        var fixture = await FixtureAsync();
        fixture.Person.ReauthenticationPausedUntil = DateTime.UtcNow.AddMinutes(-1);

        // When
        var output = await fixture.Handler().HandleAsync(fixture.Command("App"));

        // Then
        Assert.True(output.Success);
        Assert.Null(fixture.Person.ReauthenticationPausedUntil);
        Assert.Equal(0, fixture.Person.FailedReauthenticationAttempts);
    }

    // AF-36f and AF-36g (FR-2F-19, FR-PE-16): selecting the Email method mails a code, so it is charged
    // to the person's account-email budget — one per cooldown, five a day. The budget is read before
    // the password, so a request that could not send costs no derivation, and charged only once the
    // password checks out, so a wrong password never spends it.

    private static void SpendCooldown(Person person) => person.AccountEmailLastSentAt = DateTime.UtcNow.AddMinutes(-1);

    private static void SpendDay(Person person)
    {
        person.AccountEmailsSent = AccountEmailBudget.MaxPerDay;
        person.AccountEmailWindowEndsAt = DateTime.UtcNow.AddHours(10);
        person.AccountEmailLastSentAt = DateTime.UtcNow.AddHours(-2);
    }

    [UnitFact]
    public async Task GivenASendInsideTheCooldown_WhenSelectingEmail_ThenRefusedAndNothingIsMailed()
    {
        // Given the person's own requests had an account email sent a minute ago
        var fixture = await FixtureAsync();
        SpendCooldown(fixture.Person);

        // When
        var output = await fixture.Handler().HandleAsync(fixture.Command("Email"));

        // Then
        Assert.False(output.Success);
        Assert.Contains(AuthMessages.EmailSentRecently, output.Errors);
        Assert.Empty(fixture.EmailCodes.Query().ToList());
        Assert.Empty(fixture.TwoFactorAuths.Query().ToList());
        fixture.EmailSender.Verify(
            sender => sender.SendAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [UnitFact]
    public async Task GivenTheDaysAccountEmailsAreSpent_WhenSelectingEmail_ThenRefusedWithTheDailyLimit()
    {
        // Given
        var fixture = await FixtureAsync();
        SpendDay(fixture.Person);

        // When
        var output = await fixture.Handler().HandleAsync(fixture.Command("App", "Email"));

        // Then — nothing of the request applies, App included
        Assert.Contains(AuthMessages.AccountEmailLimitReached, output.Errors);
        Assert.Empty(fixture.TwoFactorAuths.Query().ToList());
    }

    [UnitFact]
    public async Task GivenASendInsideTheCooldown_WhenSelectingEmailWithAWrongPassword_ThenTheReauthenticationBudgetIsNotCharged()
    {
        // Given — a state refusal answered before the password, as AF-36a is
        var fixture = await FixtureAsync();
        SpendCooldown(fixture.Person);
        var command = fixture.Command("Email");
        command.CurrentPassword = "Wr0ng-Password-Here!";

        // When
        var output = await fixture.Handler().HandleAsync(command);

        // Then
        Assert.Contains(AuthMessages.EmailSentRecently, output.Errors);
        Assert.Equal(0, fixture.Person.FailedReauthenticationAttempts);
    }

    [UnitFact]
    public async Task GivenAWrongPassword_WhenSelectingEmail_ThenTheAccountEmailBudgetIsNotCharged()
    {
        // Given — a token without the password must not be able to spend the owner's day
        var fixture = await FixtureAsync();
        var command = fixture.Command("Email");
        command.CurrentPassword = "Wr0ng-Password-Here!";

        // When
        var output = await fixture.Handler().HandleAsync(command);

        // Then
        Assert.Contains(AuthMessages.PasswordOrCodeIncorrect, output.Errors);
        Assert.Equal(0, fixture.Person.AccountEmailsSent);
        Assert.Null(fixture.Person.AccountEmailLastSentAt);
    }

    [UnitFact]
    public async Task GivenEmailSelected_WhenSetupIsInitiated_ThenOneSendIsChargedToTheBudget()
    {
        // Given
        var fixture = await FixtureAsync();

        // When
        var output = await fixture.Handler().HandleAsync(fixture.Command("Email"));

        // Then
        Assert.True(output.Success);
        Assert.Equal(1, fixture.Person.AccountEmailsSent);
        Assert.NotNull(fixture.Person.AccountEmailLastSentAt);
    }

    [UnitFact]
    public async Task GivenASendInsideTheCooldown_WhenSelectingAppOnly_ThenSetupProceedsAndNothingIsCharged()
    {
        // Given — App alone mails nothing
        var fixture = await FixtureAsync();
        SpendCooldown(fixture.Person);

        // When
        var output = await fixture.Handler().HandleAsync(fixture.Command("App"));

        // Then
        Assert.True(output.Success);
        Assert.NotNull(output.Data!.OtpAuthUri);
        Assert.Equal(0, fixture.Person.AccountEmailsSent);
    }

    [UnitFact]
    public async Task GivenTheDaysAccountEmailsAreSpent_WhenSelectingEmail_ThenTheWaitUntilTheWindowEndsIsAdvised()
    {
        // Given — refused by the read before the password, and the wait advised all the same
        var fixture = await FixtureAsync();
        SpendDay(fixture.Person);

        // When
        await fixture.Handler().HandleAsync(fixture.Command("Email"));

        // Then
        var wait = Assert.Single(fixture.RetryAfter.Advised);
        Assert.InRange(wait, TimeSpan.FromHours(10) - TimeSpan.FromMinutes(1), TimeSpan.FromHours(10));
    }
}
