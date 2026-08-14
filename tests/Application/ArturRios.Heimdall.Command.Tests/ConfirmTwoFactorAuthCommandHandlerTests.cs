using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Heimdall.Command.Handlers;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Services;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Output;
using ArturRios.Util.Hashing;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Mock;
using Moq;
using OtpNet;

namespace ArturRios.Heimdall.Command.Tests;

// Unit tests for ConfirmTwoFactorAuthCommandHandler (UC-37): the main flow for each method
// combination, AF-37a (no pending setup — covering both "never initiated" and "caller not resolvable
// as an eligible person", the UC-37 shape of a Google User), AF-37b (appCode missing/incorrect),
// AF-37c (emailCode missing/incorrect/expired/already used), and AF-37d (already active).
public class ConfirmTwoFactorAuthCommandHandlerTests
{
    private const string Base32Secret = "JBSWY3DPEHPK3PXP";
    private const string EmailCode = "123456";

    private sealed record Fixture(
        AsyncFakeRepository<Person> Persons,
        AsyncFakeRepository<TwoFactorAuth> TwoFactorAuths,
        AsyncFakeRepository<TwoFactorEmailCode> EmailCodes,
        AsyncFakeRepository<TwoFactorRecoveryCode> RecoveryCodes,
        Mock<ITotpSecretProtector> Protector,
        Person Person)
    {
        public ConfirmTwoFactorAuthCommandHandler Handler() =>
            new(
                Persons,
                TwoFactorAuths,
                TwoFactorAuths,
                EmailCodes,
                EmailCodes,
                RecoveryCodes,
                RecoveryCodes,
                TotpVerifier());

        // The real TOTP verifier over the fixture's fake repository, not a stub: the single-use rule
        // it enforces (a code cannot be presented twice) is part of what these tests exercise.
        public TotpCodeVerifier TotpVerifier() => new(Protector.Object, TwoFactorAuths);

        public ConfirmTwoFactorAuthCommand Command(string? appCode = null, string? emailCode = null) => new()
        {
            AppCode = appCode, EmailCode = emailCode, ActingPersonId = Person.PublicId, ActingRole = (int)Roles.User
        };
    }

    private static string CurrentTotpCode() => new Totp(Base32Encoding.ToBytes(Base32Secret)).ComputeTotp();

    private static async Task<Fixture> FixtureAsync()
    {
        var persons = new AsyncFakeRepository<Person>();
        var person = new Person
        {
            PublicId = Guid.NewGuid(),
            Name = "person",
            Email = "person@test.local",
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

    private static async Task<TwoFactorAuth> SeedPendingAsync(
        Fixture fixture, bool appEnabled, bool emailEnabled)
    {
        var twoFactorAuth = new TwoFactorAuth
        {
            PersonId = fixture.Person.Id,
            IsActive = false,
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

    [UnitFact]
    public async Task GivenRecoveryCodeWritesFail_WhenHandlingConfirmTwoFactorAuth_ThenSetupIsNotActivated()
    {
        // Given a store that refuses to write recovery codes. The order the handler writes in is the
        // whole point: activating first and issuing codes afterwards would leave a caller whose
        // request reported failure with two-factor switched on and not one recovery code to their
        // name — locked out of their own account by a call that said it had not worked.
        var fixture = await FixtureAsync();
        var twoFactorAuth = await SeedPendingAsync(fixture, appEnabled: true, emailEnabled: false);
        var recoveryCodes = new FailingRecoveryCodeRepository();

        var handler = new ConfirmTwoFactorAuthCommandHandler(
            fixture.Persons,
            fixture.TwoFactorAuths,
            fixture.TwoFactorAuths,
            fixture.EmailCodes,
            fixture.EmailCodes,
            recoveryCodes,
            recoveryCodes,
            fixture.TotpVerifier());

        // When
        var output = await handler.HandleAsync(fixture.Command(CurrentTotpCode()));

        // Then — the request fails, and the configuration is left exactly as it was
        Assert.False(output.Success);
        Assert.False(Assert.Single(fixture.TwoFactorAuths.Query().ToList()).IsActive);
        Assert.False(twoFactorAuth.IsActive);
    }

    [UnitFact]
    public async Task GivenCodesLeftByAFailedAttempt_WhenHandlingConfirmTwoFactorAuth_ThenOnlyTheReturnedCodesRemain()
    {
        // Given ten recovery codes stored against a configuration that never activated — what a run
        // that wrote its codes and then failed to activate leaves behind. Nobody was ever told them,
        // and they must not survive alongside the set this attempt returns.
        var fixture = await FixtureAsync();
        var twoFactorAuth = await SeedPendingAsync(fixture, appEnabled: true, emailEnabled: false);

        for (var i = 0; i < 10; i++)
        {
            await fixture.RecoveryCodes.CreateAsync(new TwoFactorRecoveryCode
            {
                TwoFactorAuthId = twoFactorAuth.Id, CodeHash = [(byte)i], Used = false
            });
        }

        // When
        var output = await fixture.Handler().HandleAsync(fixture.Command(CurrentTotpCode()));

        // Then — ten rows, and every one of them a code the caller was just handed
        Assert.True(output.Success);

        var stored = fixture.RecoveryCodes.Query().ToList();
        Assert.Equal(10, stored.Count);

        var issued = output.Data!.RecoveryCodes.Select(HashRecoveryCode).ToList();
        Assert.All(stored, code => Assert.Contains(issued, hash => hash.SequenceEqual(code.CodeHash)));
    }

    private static byte[] HashRecoveryCode(string recoveryCode) =>
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(recoveryCode));

    /// <summary>
    ///     A recovery-code store whose writes always fail, for proving what the handler does when it
    ///     cannot issue the codes it is about to promise.
    /// </summary>
    private sealed class FailingRecoveryCodeRepository
        : AsyncFakeRepository<TwoFactorRecoveryCode>, IAsyncRepository<TwoFactorRecoveryCode>
    {
        Task<DataOutput<IEnumerable<long>>> IAsyncRepository<TwoFactorRecoveryCode>.CreateRangeAsync(
            IEnumerable<TwoFactorRecoveryCode> entities, CancellationToken cancellationToken) =>
            Task.FromResult(DataOutput<IEnumerable<long>>.New
                .WithError("recovery codes could not be written"));
    }

    [UnitFact]
    public async Task GivenValidAppAndEmailCodes_WhenHandlingConfirmTwoFactorAuth_ThenSetupIsActivatedWithTenRecoveryCodes()
    {
        // Given
        var fixture = await FixtureAsync();
        var twoFactorAuth = await SeedPendingAsync(fixture, appEnabled: true, emailEnabled: true);
        var emailCode = await SeedEmailCodeAsync(fixture, twoFactorAuth.Id);

        // When
        var output = await fixture.Handler().HandleAsync(fixture.Command(CurrentTotpCode(), EmailCode));

        // Then — response
        Assert.True(output.Success);
        Assert.Contains(TwoFactorMessages.SetupConfirmed, output.Messages);
        Assert.True(output.Data!.Enabled);
        Assert.Equal(10, output.Data.RecoveryCodes.Count);
        Assert.Equal(10, output.Data.RecoveryCodes.Distinct().Count());

        // Then — persisted state
        var stored = Assert.Single(fixture.TwoFactorAuths.Query().ToList());
        Assert.True(stored.IsActive);
        Assert.True(emailCode.Used);
        Assert.Equal(10, fixture.RecoveryCodes.Query().ToList().Count);
        Assert.All(fixture.RecoveryCodes.Query().ToList(), code => Assert.False(code.Used));
    }

    [UnitFact]
    public async Task GivenOnlyAppEnabledAndValidAppCode_WhenHandlingConfirmTwoFactorAuth_ThenSetupIsActivated()
    {
        // Given
        var fixture = await FixtureAsync();
        await SeedPendingAsync(fixture, appEnabled: true, emailEnabled: false);

        // When
        var output = await fixture.Handler().HandleAsync(fixture.Command(CurrentTotpCode()));

        // Then
        Assert.True(output.Success);
        Assert.True(Assert.Single(fixture.TwoFactorAuths.Query().ToList()).IsActive);
        Assert.Empty(fixture.EmailCodes.Query().ToList());
    }

    [UnitFact]
    public async Task GivenOnlyEmailEnabledAndValidEmailCode_WhenHandlingConfirmTwoFactorAuth_ThenSetupIsActivated()
    {
        // Given
        var fixture = await FixtureAsync();
        var twoFactorAuth = await SeedPendingAsync(fixture, appEnabled: false, emailEnabled: true);
        var emailCode = await SeedEmailCodeAsync(fixture, twoFactorAuth.Id);

        // When
        var output = await fixture.Handler().HandleAsync(fixture.Command(emailCode: EmailCode));

        // Then
        Assert.True(output.Success);
        Assert.True(Assert.Single(fixture.TwoFactorAuths.Query().ToList()).IsActive);
        Assert.True(emailCode.Used);
    }

    [UnitFact]
    public async Task GivenNoTwoFactorAuthRow_WhenHandlingConfirmTwoFactorAuth_ThenReturnsNoPendingSetupError()
    {
        // Given — AF-37a: setup was never initiated
        var fixture = await FixtureAsync();

        // When
        var output = await fixture.Handler().HandleAsync(fixture.Command(CurrentTotpCode()));

        // Then
        Assert.False(output.Success);
        Assert.Contains(TwoFactorMessages.NoPendingSetup, output.Errors);
    }

    [UnitFact]
    public async Task GivenCallerNamesNoEligiblePerson_WhenHandlingConfirmTwoFactorAuth_ThenReturnsNoPendingSetupError()
    {
        // Given — AF-37a's other shape: no live Person named by the token (Google User or hard delete)
        var fixture = await FixtureAsync();

        // When
        var output = await fixture.Handler().HandleAsync(new ConfirmTwoFactorAuthCommand
        {
            AppCode = "123456", ActingPersonId = Guid.NewGuid(), ActingRole = (int)Roles.User
        });

        // Then
        Assert.False(output.Success);
        Assert.Contains(TwoFactorMessages.NoPendingSetup, output.Errors);
    }

    [UnitFact]
    public async Task GivenAppEnabledAndMissingAppCode_WhenHandlingConfirmTwoFactorAuth_ThenReturnsAppCodeInvalidError()
    {
        // Given — AF-37b
        var fixture = await FixtureAsync();
        await SeedPendingAsync(fixture, appEnabled: true, emailEnabled: false);

        // When
        var output = await fixture.Handler().HandleAsync(fixture.Command());

        // Then
        Assert.False(output.Success);
        Assert.Contains(TwoFactorMessages.AppCodeInvalid, output.Errors);
        Assert.False(Assert.Single(fixture.TwoFactorAuths.Query().ToList()).IsActive);
    }

    [UnitFact]
    public async Task GivenAppEnabledAndIncorrectAppCode_WhenHandlingConfirmTwoFactorAuth_ThenReturnsAppCodeInvalidError()
    {
        // Given — AF-37b
        var fixture = await FixtureAsync();
        await SeedPendingAsync(fixture, appEnabled: true, emailEnabled: false);

        // When
        var output = await fixture.Handler().HandleAsync(fixture.Command("000000"));

        // Then
        Assert.False(output.Success);
        Assert.Contains(TwoFactorMessages.AppCodeInvalid, output.Errors);
    }

    [UnitFact]
    public async Task GivenEmailEnabledAndMissingEmailCode_WhenHandlingConfirmTwoFactorAuth_ThenReturnsEmailCodeInvalidError()
    {
        // Given — AF-37c
        var fixture = await FixtureAsync();
        var twoFactorAuth = await SeedPendingAsync(fixture, appEnabled: false, emailEnabled: true);
        await SeedEmailCodeAsync(fixture, twoFactorAuth.Id);

        // When
        var output = await fixture.Handler().HandleAsync(fixture.Command());

        // Then
        Assert.False(output.Success);
        Assert.Contains(TwoFactorMessages.EmailCodeInvalid, output.Errors);
        Assert.False(Assert.Single(fixture.TwoFactorAuths.Query().ToList()).IsActive);
    }

    [UnitFact]
    public async Task GivenEmailEnabledAndIncorrectEmailCode_WhenHandlingConfirmTwoFactorAuth_ThenReturnsEmailCodeInvalidError()
    {
        // Given — AF-37c
        var fixture = await FixtureAsync();
        var twoFactorAuth = await SeedPendingAsync(fixture, appEnabled: false, emailEnabled: true);
        await SeedEmailCodeAsync(fixture, twoFactorAuth.Id);

        // When
        var output = await fixture.Handler().HandleAsync(fixture.Command(emailCode: "654321"));

        // Then
        Assert.False(output.Success);
        Assert.Contains(TwoFactorMessages.EmailCodeInvalid, output.Errors);
    }

    [UnitFact]
    public async Task GivenEmailEnabledAndExpiredEmailCode_WhenHandlingConfirmTwoFactorAuth_ThenReturnsEmailCodeInvalidError()
    {
        // Given — AF-37c
        var fixture = await FixtureAsync();
        var twoFactorAuth = await SeedPendingAsync(fixture, appEnabled: false, emailEnabled: true);
        await SeedEmailCodeAsync(fixture, twoFactorAuth.Id, expired: true);

        // When
        var output = await fixture.Handler().HandleAsync(fixture.Command(emailCode: EmailCode));

        // Then
        Assert.False(output.Success);
        Assert.Contains(TwoFactorMessages.EmailCodeInvalid, output.Errors);
    }

    [UnitFact]
    public async Task GivenEmailEnabledAndAlreadyUsedEmailCode_WhenHandlingConfirmTwoFactorAuth_ThenReturnsEmailCodeInvalidError()
    {
        // Given — AF-37c
        var fixture = await FixtureAsync();
        var twoFactorAuth = await SeedPendingAsync(fixture, appEnabled: false, emailEnabled: true);
        await SeedEmailCodeAsync(fixture, twoFactorAuth.Id, used: true);

        // When
        var output = await fixture.Handler().HandleAsync(fixture.Command(emailCode: EmailCode));

        // Then
        Assert.False(output.Success);
        Assert.Contains(TwoFactorMessages.EmailCodeInvalid, output.Errors);
    }

    [UnitFact]
    public async Task GivenAlreadyActiveConfiguration_WhenHandlingConfirmTwoFactorAuth_ThenReturnsAlreadyActiveError()
    {
        // Given — AF-37d
        var fixture = await FixtureAsync();
        await fixture.TwoFactorAuths.CreateAsync(new TwoFactorAuth
        {
            PersonId = fixture.Person.Id, IsActive = true, AppEnabled = true
        });

        // When
        var output = await fixture.Handler().HandleAsync(fixture.Command(CurrentTotpCode()));

        // Then
        Assert.False(output.Success);
        Assert.Contains(TwoFactorMessages.AlreadyActive, output.Errors);
        Assert.Empty(fixture.RecoveryCodes.Query().ToList());
    }
}
