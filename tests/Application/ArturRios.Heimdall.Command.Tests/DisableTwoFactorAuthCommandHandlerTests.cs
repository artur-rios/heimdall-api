using System.Security.Cryptography;
using System.Text;
using ArturRios.Heimdall.Command.Handlers;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Services;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Util.Hashing;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Mock;
using Moq;
using OtpNet;

namespace ArturRios.Heimdall.Command.Tests;

// Unit tests for DisableTwoFactorAuthCommandHandler (UC-39): the main flow via an app code, an email
// code, and a recovery code; AF-39a (not active — covering both "no row at all" and "caller not
// resolvable as a live person", the UC-39 shape of a Google User); AF-39b (password mismatch); and
// AF-39c (second factor invalid, per AF-38b/AF-38c's shape).
public class DisableTwoFactorAuthCommandHandlerTests
{
    private const string Base32Secret = "JBSWY3DPEHPK3PXP";
    private const string EmailCode = "123456";
    private const string Password = "Str0ng-Pass!";

    private sealed record Fixture(
        AsyncFakeRepository<Person> Persons,
        AsyncFakeRepository<TwoFactorAuth> TwoFactorAuths,
        AsyncFakeRepository<TwoFactorEmailCode> EmailCodes,
        AsyncFakeRepository<TwoFactorRecoveryCode> RecoveryCodes,
        Mock<ITotpSecretProtector> Protector,
        Person Person)
    {
        public DisableTwoFactorAuthCommandHandler Handler() =>
            new(
                Persons,
                TwoFactorAuths,
                TwoFactorAuths,
                new TwoFactorFactorVerifier(EmailCodes, RecoveryCodes, Protector.Object));

        public DisableTwoFactorAuthCommand Command(
            string? password = Password, string? code = null, string? recoveryCode = null) => new()
        {
            Password = password ?? string.Empty,
            Code = code,
            RecoveryCode = recoveryCode,
            ActingPersonId = Person.PublicId,
            ActingRole = (int)Roles.User
        };
    }

    private static string CurrentTotpCode() => new Totp(Base32Encoding.ToBytes(Base32Secret)).ComputeTotp();

    private static byte[] HashRecoveryCode(string code) => SHA256.HashData(Encoding.UTF8.GetBytes(code));

    private static async Task<Fixture> FixtureAsync()
    {
        var persons = new AsyncFakeRepository<Person>();
        var person = new Person
        {
            PublicId = Guid.NewGuid(),
            Name = "person",
            Email = "person@test.local",
            PasswordHash = Hash.EncodeWithRandomSalt(Password, out var salt),
            Salt = salt,
            RoleId = (long)Roles.User
        };
        await persons.CreateAsync(person);

        var protector = new Mock<ITotpSecretProtector>();
        protector.Setup(p => p.Unprotect(It.IsAny<byte[]>())).Returns(Base32Secret);

        return new Fixture(
            persons,
            new AsyncFakeRepository<TwoFactorAuth>(),
            new AsyncFakeRepository<TwoFactorEmailCode>(),
            new AsyncFakeRepository<TwoFactorRecoveryCode>(),
            protector,
            person);
    }

    private static async Task<TwoFactorAuth> SeedActiveAsync(Fixture fixture, bool appEnabled, bool emailEnabled)
    {
        var twoFactorAuth = new TwoFactorAuth
        {
            PersonId = fixture.Person.Id,
            IsActive = true,
            AppEnabled = appEnabled,
            EmailEnabled = emailEnabled,
            TotpSecretEncrypted = appEnabled ? [1, 2, 3] : null
        };
        await fixture.TwoFactorAuths.CreateAsync(twoFactorAuth);

        return twoFactorAuth;
    }

    private static async Task<TwoFactorEmailCode> SeedEmailCodeAsync(
        Fixture fixture, long twoFactorAuthId, bool used = false, bool expired = false)
    {
        var codeHash = Hash.EncodeWithRandomSalt(EmailCode, out var salt);
        var emailCode = new TwoFactorEmailCode
        {
            TwoFactorAuthId = twoFactorAuthId,
            CodeHash = codeHash,
            Salt = salt,
            ExpiresAt = expired ? DateTime.UtcNow.AddMinutes(-1) : DateTime.UtcNow.AddMinutes(10),
            Used = used
        };
        await fixture.EmailCodes.CreateAsync(emailCode);

        return emailCode;
    }

    private static async Task<TwoFactorRecoveryCode> SeedRecoveryCodeAsync(
        Fixture fixture, long twoFactorAuthId, string plaintext, bool used = false)
    {
        var recoveryCode = new TwoFactorRecoveryCode
        {
            TwoFactorAuthId = twoFactorAuthId,
            CodeHash = HashRecoveryCode(plaintext),
            Used = used,
            UsedAt = used ? DateTime.UtcNow : null
        };
        await fixture.RecoveryCodes.CreateAsync(recoveryCode);

        return recoveryCode;
    }

    [UnitFact]
    public async Task GivenValidPasswordAndAppCode_WhenHandlingDisable_ThenTwoFactorAuthIsRemoved()
    {
        // Given
        var fixture = await FixtureAsync();
        await SeedActiveAsync(fixture, appEnabled: true, emailEnabled: false);

        // When
        var output = await fixture.Handler().HandleAsync(fixture.Command(code: CurrentTotpCode()));

        // Then
        Assert.True(output.Success);
        Assert.True(output.Data!.Disabled);
        Assert.Contains(TwoFactorMessages.Disabled, output.Messages);
        Assert.Empty(fixture.TwoFactorAuths.Query().ToList());
    }

    [UnitFact]
    public async Task GivenValidPasswordAndEmailCode_WhenHandlingDisable_ThenTwoFactorAuthIsRemoved()
    {
        // Given
        var fixture = await FixtureAsync();
        var twoFactorAuth = await SeedActiveAsync(fixture, appEnabled: false, emailEnabled: true);
        await SeedEmailCodeAsync(fixture, twoFactorAuth.Id);

        // When
        var output = await fixture.Handler().HandleAsync(fixture.Command(code: EmailCode));

        // Then
        Assert.True(output.Success);
        Assert.True(output.Data!.Disabled);
        Assert.Empty(fixture.TwoFactorAuths.Query().ToList());
    }

    [UnitFact]
    public async Task GivenValidPasswordAndRecoveryCode_WhenHandlingDisable_ThenTwoFactorAuthIsRemoved()
    {
        // Given
        var fixture = await FixtureAsync();
        var twoFactorAuth = await SeedActiveAsync(fixture, appEnabled: true, emailEnabled: false);
        const string plaintext = "AAAA-BBBB";
        await SeedRecoveryCodeAsync(fixture, twoFactorAuth.Id, plaintext);

        // When
        var output = await fixture.Handler().HandleAsync(fixture.Command(recoveryCode: plaintext));

        // Then
        Assert.True(output.Success);
        Assert.True(output.Data!.Disabled);
        Assert.Empty(fixture.TwoFactorAuths.Query().ToList());
    }

    [UnitFact]
    public async Task GivenNoTwoFactorAuthRow_WhenHandlingDisable_ThenReturnsNotActiveError()
    {
        // Given — AF-39a: two-factor authentication was never enabled
        var fixture = await FixtureAsync();

        // When
        var output = await fixture.Handler().HandleAsync(fixture.Command(code: CurrentTotpCode()));

        // Then
        Assert.False(output.Success);
        Assert.Contains(TwoFactorMessages.NotActive, output.Errors);
    }

    [UnitFact]
    public async Task GivenPendingNotYetActiveTwoFactorAuth_WhenHandlingDisable_ThenReturnsNotActiveError()
    {
        // Given — AF-39a: a row exists (UC-36 initiated it) but was never confirmed by UC-37
        var fixture = await FixtureAsync();
        await fixture.TwoFactorAuths.CreateAsync(new TwoFactorAuth
        {
            PersonId = fixture.Person.Id, IsActive = false, AppEnabled = true, TotpSecretEncrypted = [1, 2, 3]
        });

        // When
        var output = await fixture.Handler().HandleAsync(fixture.Command(code: CurrentTotpCode()));

        // Then
        Assert.False(output.Success);
        Assert.Contains(TwoFactorMessages.NotActive, output.Errors);
    }

    [UnitFact]
    public async Task GivenCallerNamesNoEligiblePerson_WhenHandlingDisable_ThenReturnsNotActiveError()
    {
        // Given — AF-39a's other shape: no live Person named by the token (Google User or hard delete)
        var fixture = await FixtureAsync();
        await SeedActiveAsync(fixture, appEnabled: true, emailEnabled: false);

        // When
        var output = await fixture.Handler().HandleAsync(new DisableTwoFactorAuthCommand
        {
            Password = Password,
            Code = CurrentTotpCode(),
            ActingPersonId = Guid.NewGuid(),
            ActingRole = (int)Roles.User
        });

        // Then
        Assert.False(output.Success);
        Assert.Contains(TwoFactorMessages.NotActive, output.Errors);
    }

    [UnitFact]
    public async Task GivenWrongPassword_WhenHandlingDisable_ThenReturnsPasswordMismatchErrorAndRowSurvives()
    {
        // Given — AF-39b
        var fixture = await FixtureAsync();
        await SeedActiveAsync(fixture, appEnabled: true, emailEnabled: false);

        // When
        var output = await fixture.Handler().HandleAsync(
            fixture.Command(password: "wrong-password", code: CurrentTotpCode()));

        // Then
        Assert.False(output.Success);
        Assert.Contains(TwoFactorMessages.PasswordMismatch, output.Errors);
        Assert.Single(fixture.TwoFactorAuths.Query().ToList());
    }

    [UnitFact]
    public async Task GivenCorrectPasswordAndWrongAppCode_WhenHandlingDisable_ThenReturnsFactorInvalidErrorAndRowSurvives()
    {
        // Given — AF-39c
        var fixture = await FixtureAsync();
        await SeedActiveAsync(fixture, appEnabled: true, emailEnabled: false);

        // When
        var output = await fixture.Handler().HandleAsync(fixture.Command(code: "000000"));

        // Then
        Assert.False(output.Success);
        Assert.Contains(TwoFactorMessages.FactorInvalid, output.Errors);
        Assert.Single(fixture.TwoFactorAuths.Query().ToList());
    }

    [UnitFact]
    public async Task GivenCorrectPasswordAndAlreadyUsedRecoveryCode_WhenHandlingDisable_ThenReturnsFactorInvalidError()
    {
        // Given — AF-39c's recovery-code shape
        var fixture = await FixtureAsync();
        var twoFactorAuth = await SeedActiveAsync(fixture, appEnabled: true, emailEnabled: false);
        const string plaintext = "CCCC-DDDD";
        await SeedRecoveryCodeAsync(fixture, twoFactorAuth.Id, plaintext, used: true);

        // When
        var output = await fixture.Handler().HandleAsync(fixture.Command(recoveryCode: plaintext));

        // Then
        Assert.False(output.Success);
        Assert.Contains(TwoFactorMessages.FactorInvalid, output.Errors);
        Assert.Single(fixture.TwoFactorAuths.Query().ToList());
    }

    [UnitFact]
    public async Task GivenCorrectPasswordAndNoCodeOrRecoveryCode_WhenHandlingDisable_ThenReturnsFactorInvalidError()
    {
        // Given — AF-39c: neither a code nor a recovery code submitted
        var fixture = await FixtureAsync();
        await SeedActiveAsync(fixture, appEnabled: true, emailEnabled: true);

        // When
        var output = await fixture.Handler().HandleAsync(fixture.Command());

        // Then
        Assert.False(output.Success);
        Assert.Contains(TwoFactorMessages.FactorInvalid, output.Errors);
    }
}
