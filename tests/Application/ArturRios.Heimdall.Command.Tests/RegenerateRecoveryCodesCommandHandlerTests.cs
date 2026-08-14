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

// Unit tests for RegenerateRecoveryCodesCommandHandler (UC-40): the main flow via an app code, an
// email code, and a recovery code, each confirming the previous ten recovery codes no longer
// validate and exactly ten new ones exist; AF-40a (not active — covering both "no row at all" and
// "caller not resolvable as a live person", the UC-40 shape of a Google User); and AF-40b (second
// factor invalid, per AF-38b/AF-38c's shape).
public class RegenerateRecoveryCodesCommandHandlerTests
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
        public RegenerateRecoveryCodesCommandHandler Handler() =>
            new(
                Persons,
                TwoFactorAuths,
                RecoveryCodes,
                RecoveryCodes,
                new TwoFactorFactorVerifier(EmailCodes, EmailCodes, RecoveryCodes, TotpVerifier()));

        // The real TOTP verifier over the fixture's fake repository, not a stub: the single-use rule
        // it enforces (a code cannot be presented twice) is part of what these tests exercise.
        public TotpCodeVerifier TotpVerifier() => new(Protector.Object, TwoFactorAuths);

        public RegenerateRecoveryCodesCommand Command(string? code = null, string? recoveryCode = null) => new()
        {
            Code = code, RecoveryCode = recoveryCode, ActingPersonId = Person.PublicId, ActingRole = (int)Roles.User
        };
    }

    private static string CurrentTotpCode() => new Totp(Base32Encoding.ToBytes(Base32Secret)).ComputeTotp();

    private static byte[] HashRecoveryCode(string code) => SHA256.HashData(Encoding.UTF8.GetBytes(code));

    private static async Task<Fixture> FixtureAsync()
    {
        var persons = new AsyncFakeRepository<Person>();
        var person = new Person
        {
            PublicId = Guid.NewGuid(), Name = "person", Email = "person@test.local", RoleId = (long)Roles.User
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
    public async Task GivenValidAppCode_WhenHandlingRegenerate_ThenOldCodesAreReplacedWithTenNewOnes()
    {
        // Given an active configuration with two pre-existing recovery codes, one already used
        var fixture = await FixtureAsync();
        var twoFactorAuth = await SeedActiveAsync(fixture, appEnabled: true, emailEnabled: false);
        const string oldUnused = "AAAA-BBBB";
        const string oldUsed = "CCCC-DDDD";
        await SeedRecoveryCodeAsync(fixture, twoFactorAuth.Id, oldUnused);
        await SeedRecoveryCodeAsync(fixture, twoFactorAuth.Id, oldUsed, used: true);

        // When
        var output = await fixture.Handler().HandleAsync(fixture.Command(code: CurrentTotpCode()));

        // Then — response
        Assert.True(output.Success);
        Assert.Contains(TwoFactorMessages.RecoveryCodesRegenerated, output.Messages);
        Assert.Equal(10, output.Data!.RecoveryCodes.Count);
        Assert.Equal(10, output.Data.RecoveryCodes.Distinct().Count());

        // Then — the stored rows are exactly the ten new ones; the old two are gone
        var storedCodes = fixture.RecoveryCodes.Query().Where(x => x.TwoFactorAuthId == twoFactorAuth.Id).ToList();
        Assert.Equal(10, storedCodes.Count);
        Assert.All(storedCodes, x => Assert.False(x.Used));

        var oldUnusedHash = HashRecoveryCode(oldUnused);
        var oldUsedHash = HashRecoveryCode(oldUsed);
        Assert.DoesNotContain(storedCodes, x => x.CodeHash.SequenceEqual(oldUnusedHash));
        Assert.DoesNotContain(storedCodes, x => x.CodeHash.SequenceEqual(oldUsedHash));

        // Then — the old, still-unused code no longer verifies as a valid second factor
        var verifier = new TwoFactorFactorVerifier(
            fixture.EmailCodes, fixture.EmailCodes, fixture.RecoveryCodes, fixture.TotpVerifier());
        var replay = await verifier.VerifyAsync(twoFactorAuth, code: null, recoveryCode: oldUnused);
        Assert.False(replay.Matched);
    }

    [UnitFact]
    public async Task GivenValidEmailCode_WhenHandlingRegenerate_ThenTenNewCodesAreIssued()
    {
        // Given
        var fixture = await FixtureAsync();
        var twoFactorAuth = await SeedActiveAsync(fixture, appEnabled: false, emailEnabled: true);
        await SeedEmailCodeAsync(fixture, twoFactorAuth.Id);

        // When
        var output = await fixture.Handler().HandleAsync(fixture.Command(code: EmailCode));

        // Then
        Assert.True(output.Success);
        Assert.Equal(10, output.Data!.RecoveryCodes.Count);
        Assert.Equal(10, fixture.RecoveryCodes.Query().Count(x => x.TwoFactorAuthId == twoFactorAuth.Id));
    }

    [UnitFact]
    public async Task GivenValidRecoveryCode_WhenHandlingRegenerate_ThenTenNewCodesReplaceTheOldTen()
    {
        // Given
        var fixture = await FixtureAsync();
        var twoFactorAuth = await SeedActiveAsync(fixture, appEnabled: true, emailEnabled: false);
        const string usedForRegeneration = "EEEE-FFFF";
        await SeedRecoveryCodeAsync(fixture, twoFactorAuth.Id, usedForRegeneration);

        // When
        var output = await fixture.Handler().HandleAsync(fixture.Command(recoveryCode: usedForRegeneration));

        // Then
        Assert.True(output.Success);
        Assert.Equal(10, output.Data!.RecoveryCodes.Count);

        var storedCodes = fixture.RecoveryCodes.Query().Where(x => x.TwoFactorAuthId == twoFactorAuth.Id).ToList();
        Assert.Equal(10, storedCodes.Count);
        var usedHash = HashRecoveryCode(usedForRegeneration);
        Assert.DoesNotContain(storedCodes, x => x.CodeHash.SequenceEqual(usedHash));
    }

    [UnitFact]
    public async Task GivenNoTwoFactorAuthRow_WhenHandlingRegenerate_ThenReturnsNotActiveError()
    {
        // Given — AF-40a: two-factor authentication was never enabled
        var fixture = await FixtureAsync();

        // When
        var output = await fixture.Handler().HandleAsync(fixture.Command(code: CurrentTotpCode()));

        // Then
        Assert.False(output.Success);
        Assert.Contains(TwoFactorMessages.NotActive, output.Errors);
    }

    [UnitFact]
    public async Task GivenPendingNotYetActiveTwoFactorAuth_WhenHandlingRegenerate_ThenReturnsNotActiveError()
    {
        // Given — AF-40a: a row exists (UC-36 initiated it) but was never confirmed by UC-37
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
    public async Task GivenCallerNamesNoEligiblePerson_WhenHandlingRegenerate_ThenReturnsNotActiveError()
    {
        // Given — AF-40a's other shape: no live Person named by the token (Google User or hard delete)
        var fixture = await FixtureAsync();
        await SeedActiveAsync(fixture, appEnabled: true, emailEnabled: false);

        // When
        var output = await fixture.Handler().HandleAsync(new RegenerateRecoveryCodesCommand
        {
            Code = CurrentTotpCode(), ActingPersonId = Guid.NewGuid(), ActingRole = (int)Roles.User
        });

        // Then
        Assert.False(output.Success);
        Assert.Contains(TwoFactorMessages.NotActive, output.Errors);
    }

    [UnitFact]
    public async Task GivenWrongAppCode_WhenHandlingRegenerate_ThenReturnsFactorInvalidErrorAndOldCodesSurvive()
    {
        // Given — AF-40b
        var fixture = await FixtureAsync();
        var twoFactorAuth = await SeedActiveAsync(fixture, appEnabled: true, emailEnabled: false);
        const string plaintext = "GGGG-HHHH";
        await SeedRecoveryCodeAsync(fixture, twoFactorAuth.Id, plaintext);

        // When
        var output = await fixture.Handler().HandleAsync(fixture.Command(code: "000000"));

        // Then
        Assert.False(output.Success);
        Assert.Contains(TwoFactorMessages.FactorInvalid, output.Errors);
        Assert.Single(fixture.RecoveryCodes.Query().Where(x => x.TwoFactorAuthId == twoFactorAuth.Id).ToList());
    }

    [UnitFact]
    public async Task GivenAlreadyUsedRecoveryCode_WhenHandlingRegenerate_ThenReturnsFactorInvalidError()
    {
        // Given — AF-40b's recovery-code shape
        var fixture = await FixtureAsync();
        var twoFactorAuth = await SeedActiveAsync(fixture, appEnabled: true, emailEnabled: false);
        const string plaintext = "IIII-JJJJ";
        await SeedRecoveryCodeAsync(fixture, twoFactorAuth.Id, plaintext, used: true);

        // When
        var output = await fixture.Handler().HandleAsync(fixture.Command(recoveryCode: plaintext));

        // Then
        Assert.False(output.Success);
        Assert.Contains(TwoFactorMessages.FactorInvalid, output.Errors);
        Assert.Single(fixture.RecoveryCodes.Query().Where(x => x.TwoFactorAuthId == twoFactorAuth.Id).ToList());
    }

    [UnitFact]
    public async Task GivenNoCodeOrRecoveryCode_WhenHandlingRegenerate_ThenReturnsFactorInvalidError()
    {
        // Given — AF-40b: neither a code nor a recovery code submitted
        var fixture = await FixtureAsync();
        await SeedActiveAsync(fixture, appEnabled: true, emailEnabled: true);

        // When
        var output = await fixture.Handler().HandleAsync(fixture.Command());

        // Then
        Assert.False(output.Success);
        Assert.Contains(TwoFactorMessages.FactorInvalid, output.Errors);
    }
}
