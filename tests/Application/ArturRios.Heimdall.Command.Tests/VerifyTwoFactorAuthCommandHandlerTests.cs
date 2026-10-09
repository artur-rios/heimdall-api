using System.Security.Cryptography;
using System.Text;
using ArturRios.Heimdall.Command.Handlers;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Services;
using ArturRios.Heimdall.Command.Tests.Support;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Util.Hashing;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Mock;
using Moq;
using OtpNet;

namespace ArturRios.Heimdall.Command.Tests;

// Unit tests for VerifyTwoFactorAuthCommandHandler (UC-38): the main flow via an app code, an email
// code, and a recovery code; AF-38a (invalid/expired/non-challenge token, and the edge case where the
// named person or configuration no longer qualifies); AF-38b (wrong/missing code); and AF-38c (an
// already-used recovery code answering identically to AF-38b).
public class VerifyTwoFactorAuthCommandHandlerTests
{
    private const string Base32Secret = "JBSWY3DPEHPK3PXP";
    private const string EmailCode = "123456";

    /// <summary>A validator that resolves a fixed principal, or none at all (AF-38a).</summary>
    private sealed class StubChallengeTokenValidator(Guid? personId, Guid challengeId) : ITwoFactorChallengeTokenValidator
    {
        public Task<TwoFactorChallengePrincipal?> ValidateAsync(string? token) =>
            Task.FromResult(personId is null ? null : new TwoFactorChallengePrincipal(personId.Value, challengeId));
    }

    /// <summary>The challenge every seeded configuration has outstanding, and every stub token names.</summary>
    private static readonly Guid OutstandingChallenge = Guid.NewGuid();

    private sealed class RecordingIssuer : IAuthTokenIssuer
    {
        public AuthTokenSubject? Subject { get; private set; }

        public Task<AuthToken> IssueAsync(AuthTokenSubject subject)
        {
            Subject = subject;
            return Task.FromResult(
                new AuthToken("full-token", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        }
    }

    private sealed record Fixture(
        AsyncFakeRepository<Person, long> Persons,
        AsyncFakeRepository<TwoFactorAuth, long> TwoFactorAuths,
        AsyncFakeRepository<TwoFactorEmailCode, long> EmailCodes,
        AsyncFakeRepository<TwoFactorRecoveryCode, long> RecoveryCodes,
        Mock<ITotpSecretProtector> Protector,
        RecordingIssuer TokenIssuer,
        Person Person)
    {
        public VerifyTwoFactorAuthCommandHandler Handler(Guid? challengePersonId, Guid? challengeId = null) =>
            new(
                Persons,
                TwoFactorAuths,
                InMemoryAtomicWrites.Instance,
                new TwoFactorFactorVerifier(EmailCodes, InMemoryAtomicWrites.Instance, RecoveryCodes, TotpVerifier()),
                new StubChallengeTokenValidator(challengePersonId ?? Person.PublicId, challengeId ?? OutstandingChallenge),
                new PersonAuthTokenService(TokenIssuer));

        public VerifyTwoFactorAuthCommandHandler HandlerForInvalidToken() =>
            new(
                Persons,
                TwoFactorAuths,
                InMemoryAtomicWrites.Instance,
                new TwoFactorFactorVerifier(EmailCodes, InMemoryAtomicWrites.Instance, RecoveryCodes, TotpVerifier()),
                new StubChallengeTokenValidator(null, OutstandingChallenge),
                new PersonAuthTokenService(TokenIssuer));

        // The real TOTP verifier over the fixture's fake repository, not a stub: the single-use rule
        // it enforces (a code cannot be presented twice) is part of what these tests exercise.
        public TotpCodeVerifier TotpVerifier() => new(Protector.Object, InMemoryAtomicWrites.Instance);

        public VerifyTwoFactorAuthCommand Command(string? code = null, string? recoveryCode = null) => new()
        {
            ChallengeToken = "irrelevant-here", Code = code, RecoveryCode = recoveryCode
        };
    }

    private static string CurrentTotpCode() => new Totp(Base32Encoding.ToBytes(Base32Secret)).ComputeTotp();

    private static byte[] HashRecoveryCode(string code) => SHA256.HashData(Encoding.UTF8.GetBytes(code));

    private static async Task<Fixture> FixtureAsync()
    {
        var persons = new AsyncFakeRepository<Person, long>();
        var person = new Person
        {
            PublicId = Guid.NewGuid(),
            Name = "person",
            Email = "person@test.local",
            RoleId = (long)Roles.SystemAdmin
        };
        await persons.CreateAsync(person);

        var protector = new Mock<ITotpSecretProtector>();
        protector.Setup(p => p.Unprotect(It.IsAny<byte[]>())).Returns(Base32Secret);

        return new Fixture(
            persons,
            new AsyncFakeRepository<TwoFactorAuth, long>(),
            new AsyncFakeRepository<TwoFactorEmailCode, long>(),
            new AsyncFakeRepository<TwoFactorRecoveryCode, long>(),
            protector,
            new RecordingIssuer(),
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
            TotpSecretEncrypted = appEnabled ? [1, 2, 3] : null,
            ChallengeId = OutstandingChallenge
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
    public async Task GivenFiveWrongEmailCodeGuesses_WhenHandlingVerify_ThenTheCodeIsRetired()
    {
        // Given an active email-only configuration with a live code. Six digits is a million values
        // over a ten-minute life, and the per-IP limiter does not bound an attacker spread across
        // many addresses — so each issued code gets a small, fixed budget instead.
        var fixture = await FixtureAsync();
        var twoFactorAuth = await SeedActiveAsync(fixture, appEnabled: false, emailEnabled: true);
        var emailCode = await SeedEmailCodeAsync(fixture, twoFactorAuth.Id);

        // When five wrong guesses are made
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var wrong = await fixture.Handler(null).HandleAsync(fixture.Command(code: "000000"));

            Assert.False(wrong.Success);
            Assert.Contains(TwoFactorMessages.FactorInvalid, wrong.Errors);
        }

        // Then the code is spent, even though it was never guessed right and has not expired
        Assert.True(emailCode.Used);
        Assert.Equal(5, emailCode.FailedAttempts);

        // Then — and the real code no longer works either: the budget is the code's, not the
        // attacker's, so guessing further costs a fresh login rather than nothing
        var output = await fixture.Handler(null).HandleAsync(fixture.Command(code: EmailCode));

        Assert.False(output.Success);
        Assert.Contains(TwoFactorMessages.FactorInvalid, output.Errors);
    }

    [UnitFact]
    public async Task GivenFourWrongGuessesThenTheRightCode_WhenHandlingVerify_ThenTheTokenIsIssued()
    {
        // Given a caller who fat-fingers the code a few times before getting it right, which must
        // still work — the cap is there to stop a million guesses, not four.
        var fixture = await FixtureAsync();
        var twoFactorAuth = await SeedActiveAsync(fixture, appEnabled: false, emailEnabled: true);
        var emailCode = await SeedEmailCodeAsync(fixture, twoFactorAuth.Id);

        for (var attempt = 0; attempt < 4; attempt++)
        {
            await fixture.Handler(null).HandleAsync(fixture.Command(code: "000000"));
        }

        Assert.False(emailCode.Used);

        // When
        var output = await fixture.Handler(null).HandleAsync(fixture.Command(code: EmailCode));

        // Then
        Assert.True(output.Success);
        Assert.NotNull(output.Data!.Token);
        Assert.True(emailCode.Used);
    }

    [UnitFact]
    public async Task GivenFiveWrongAppCodes_WhenHandlingVerify_ThenTheChallengeIsRetired()
    {
        // Given an app-enabled configuration. A TOTP code has a million values too, but no row of its
        // own to count guesses on — so before FR-2F-17 only the per-IP limiter stood in the way, for
        // the challenge's whole ten minutes.
        var fixture = await FixtureAsync();
        var twoFactorAuth = await SeedActiveAsync(fixture, appEnabled: true, emailEnabled: false);

        // When five wrong guesses are made
        for (var attempt = 0; attempt < VerifyTwoFactorAuthCommandHandler.MaxGuessesPerChallenge; attempt++)
        {
            var wrong = await fixture.Handler(null).HandleAsync(fixture.Command(code: "000000"));

            Assert.Contains(TwoFactorMessages.FactorInvalid, wrong.Errors);
        }

        // Then the challenge is gone, and the right code no longer redeems it: guessing on costs a
        // fresh password check
        Assert.Null(twoFactorAuth.ChallengeId);

        var output = await fixture.Handler(null).HandleAsync(fixture.Command(code: CurrentTotpCode()));

        Assert.False(output.Success);
        Assert.Contains(TwoFactorMessages.ChallengeTokenInvalid, output.Errors);
        Assert.Null(fixture.TokenIssuer.Subject);
    }

    [UnitFact]
    public async Task GivenFiveWrongRecoveryCodes_WhenHandlingVerify_ThenTheChallengeIsRetired()
    {
        // Given an email-only configuration holding a recovery code: a recovery-code guess is charged
        // to the challenge whatever the methods are, since a guessed code matches no row to count on
        var fixture = await FixtureAsync();
        var twoFactorAuth = await SeedActiveAsync(fixture, appEnabled: false, emailEnabled: true);
        await SeedRecoveryCodeAsync(fixture, twoFactorAuth.Id, "GOOD-CODE");

        // When
        for (var attempt = 0; attempt < VerifyTwoFactorAuthCommandHandler.MaxGuessesPerChallenge; attempt++)
        {
            await fixture.Handler(null).HandleAsync(fixture.Command(recoveryCode: $"BAD-{attempt}"));
        }

        var output = await fixture.Handler(null).HandleAsync(fixture.Command(recoveryCode: "GOOD-CODE"));

        // Then
        Assert.Contains(TwoFactorMessages.ChallengeTokenInvalid, output.Errors);
        Assert.Null(twoFactorAuth.ChallengeId);
    }

    [UnitFact]
    public async Task GivenFourWrongAppCodesThenTheRightOne_WhenHandlingVerify_ThenTheTokenIsIssued()
    {
        // Given the boundary: the fifth guess is still allowed, and a right one redeems
        var fixture = await FixtureAsync();
        var twoFactorAuth = await SeedActiveAsync(fixture, appEnabled: true, emailEnabled: false);

        for (var attempt = 0; attempt < VerifyTwoFactorAuthCommandHandler.MaxGuessesPerChallenge - 1; attempt++)
        {
            await fixture.Handler(null).HandleAsync(fixture.Command(code: "000000"));
        }

        // When
        var output = await fixture.Handler(null).HandleAsync(fixture.Command(code: CurrentTotpCode()));

        // Then
        Assert.True(output.Success);
        Assert.Null(twoFactorAuth.ChallengeId);
    }

    [UnitFact]
    public async Task GivenAnEmailOnlyConfiguration_WhenGuessingWrong_ThenOnlyTheCodesOwnBudgetIsCharged()
    {
        // Given an email-only configuration: its guesses already have FR-2F-13's cap, per code and
        // with at most three reissues, and charging the challenge too would cut that to five overall
        var fixture = await FixtureAsync();
        var twoFactorAuth = await SeedActiveAsync(fixture, appEnabled: false, emailEnabled: true);
        var emailCode = await SeedEmailCodeAsync(fixture, twoFactorAuth.Id);

        // When
        await fixture.Handler(null).HandleAsync(fixture.Command(code: "000000"));

        // Then
        Assert.Equal(0, twoFactorAuth.ChallengeAttempts);
        Assert.Equal(1, emailCode.FailedAttempts);
    }

    [UnitFact]
    public async Task GivenARecoveryCodeSpentElsewhereMeanwhile_WhenHandlingVerify_ThenNoTokenIsIssued()
    {
        // Given a recovery code that matched, but that another request spends between the match and
        // this one's write — the in-memory stand-in for two requests racing on one code
        var fixture = await FixtureAsync();
        var twoFactorAuth = await SeedActiveAsync(fixture, appEnabled: false, emailEnabled: true);
        var recoveryCode = await SeedRecoveryCodeAsync(fixture, twoFactorAuth.Id, "RACE-CODE");
        var verifier = new Mock<ITwoFactorFactorVerifier>();
        verifier
            .Setup(v => v.VerifyAsync(twoFactorAuth, null, "RACE-CODE"))
            .ReturnsAsync(() =>
            {
                recoveryCode.Used = true;
                return TwoFactorFactorVerificationResult.ForRecoveryCode(recoveryCode);
            });
        var handler = new VerifyTwoFactorAuthCommandHandler(
            fixture.Persons, fixture.TwoFactorAuths, InMemoryAtomicWrites.Instance, verifier.Object,
            new StubChallengeTokenValidator(fixture.Person.PublicId, OutstandingChallenge),
            new PersonAuthTokenService(fixture.TokenIssuer));

        // When
        var output = await handler.HandleAsync(fixture.Command(recoveryCode: "RACE-CODE"));

        // Then — the loser gets no token
        Assert.False(output.Success);
        Assert.Contains(TwoFactorMessages.FactorInvalid, output.Errors);
        Assert.Null(fixture.TokenIssuer.Subject);
    }

    [UnitFact]
    public async Task GivenValidAppCode_WhenHandlingVerify_ThenFullTokenIsIssued()
    {
        // Given
        var fixture = await FixtureAsync();
        await SeedActiveAsync(fixture, appEnabled: true, emailEnabled: false);

        // When
        var output = await fixture.Handler(null).HandleAsync(fixture.Command(CurrentTotpCode()));

        // Then
        Assert.True(output.Success);
        Assert.Equal("full-token", output.Data!.Token);
        Assert.Contains(TwoFactorMessages.VerificationSuccessful, output.Messages);
        Assert.Equal(fixture.Person.PublicId, fixture.TokenIssuer.Subject!.PersonId);
    }

    [UnitFact]
    public async Task GivenValidEmailCode_WhenHandlingVerify_ThenFullTokenIsIssuedAndCodeIsConsumed()
    {
        // Given
        var fixture = await FixtureAsync();
        var twoFactorAuth = await SeedActiveAsync(fixture, appEnabled: false, emailEnabled: true);
        var emailCode = await SeedEmailCodeAsync(fixture, twoFactorAuth.Id);

        // When
        var output = await fixture.Handler(null).HandleAsync(fixture.Command(EmailCode));

        // Then
        Assert.True(output.Success);
        Assert.Equal("full-token", output.Data!.Token);
        Assert.True(emailCode.Used);
    }

    [UnitFact]
    public async Task GivenValidRecoveryCode_WhenHandlingVerify_ThenFullTokenIsIssuedAndCodeIsMarkedUsed()
    {
        // Given
        var fixture = await FixtureAsync();
        var twoFactorAuth = await SeedActiveAsync(fixture, appEnabled: true, emailEnabled: false);
        const string plaintext = "AAAA-BBBB";
        var recoveryCode = await SeedRecoveryCodeAsync(fixture, twoFactorAuth.Id, plaintext);

        // When
        var output = await fixture.Handler(null).HandleAsync(fixture.Command(recoveryCode: plaintext));

        // Then
        Assert.True(output.Success);
        Assert.Equal("full-token", output.Data!.Token);
        Assert.True(recoveryCode.Used);
        Assert.NotNull(recoveryCode.UsedAt);
    }

    [UnitFact]
    public async Task GivenARedeemedChallenge_WhenPresentedAgainWithAnotherFactor_ThenReturnsChallengeTokenInvalidError()
    {
        // Given — FR-2F-10: a challenge already traded for a full token, presented again with a
        // second, equally valid factor (a recovery code)
        var fixture = await FixtureAsync();
        var twoFactorAuth = await SeedActiveAsync(fixture, appEnabled: true, emailEnabled: false);
        var recoveryCode = await SeedRecoveryCodeAsync(fixture, twoFactorAuth.Id, "AAAA-BBBB");

        var first = await fixture.Handler(null).HandleAsync(fixture.Command(CurrentTotpCode()));
        Assert.True(first.Success);
        Assert.Null(twoFactorAuth.ChallengeId);

        // When
        var replay = await fixture.Handler(null).HandleAsync(fixture.Command(recoveryCode: "AAAA-BBBB"));

        // Then — refused before the factor is looked at, so the recovery code is not spent either
        Assert.False(replay.Success);
        Assert.Contains(TwoFactorMessages.ChallengeTokenInvalid, replay.Errors);
        Assert.False(recoveryCode.Used);
    }

    [UnitFact]
    public async Task GivenAChallengeSupersededByANewerLogin_WhenHandlingVerify_ThenReturnsChallengeTokenInvalidError()
    {
        // Given — FR-2F-10: the token names a challenge, but a later login replaced it
        var fixture = await FixtureAsync();
        await SeedActiveAsync(fixture, appEnabled: true, emailEnabled: false);

        // When
        var output = await fixture.Handler(null, challengeId: Guid.NewGuid())
            .HandleAsync(fixture.Command(CurrentTotpCode()));

        // Then
        Assert.False(output.Success);
        Assert.Contains(TwoFactorMessages.ChallengeTokenInvalid, output.Errors);
        Assert.Null(fixture.TokenIssuer.Subject);
    }

    [UnitFact]
    public async Task GivenAPersonRestrictedSinceTheirChallenge_WhenHandlingVerify_ThenNoTokenIsIssued()
    {
        // Given — NFR-24 / UC-44 step 4: restricted after UC-11 issued the challenge
        var fixture = await FixtureAsync();
        await SeedActiveAsync(fixture, appEnabled: true, emailEnabled: false);
        fixture.Person.ProcessingRestrictedAt = DateTime.UtcNow;

        // When
        var output = await fixture.Handler(null).HandleAsync(fixture.Command(CurrentTotpCode()));

        // Then
        Assert.False(output.Success);
        Assert.Contains(TwoFactorMessages.ChallengeTokenInvalid, output.Errors);
        Assert.Null(fixture.TokenIssuer.Subject);
    }

    [UnitFact]
    public async Task GivenInvalidChallengeToken_WhenHandlingVerify_ThenReturnsChallengeTokenInvalidError()
    {
        // Given — AF-38a
        var fixture = await FixtureAsync();
        await SeedActiveAsync(fixture, appEnabled: true, emailEnabled: false);

        // When
        var output = await fixture.HandlerForInvalidToken().HandleAsync(fixture.Command(CurrentTotpCode()));

        // Then
        Assert.False(output.Success);
        Assert.Contains(TwoFactorMessages.ChallengeTokenInvalid, output.Errors);
        Assert.Null(fixture.TokenIssuer.Subject);
    }

    [UnitFact]
    public async Task GivenChallengeTokenNamingNoActiveTwoFactorAuth_WhenHandlingVerify_ThenReturnsChallengeTokenInvalidError()
    {
        // Given — AF-38a's edge case: the challenge is validly signed, but there is no active
        // TwoFactorAuth row to check the code against any more (e.g. it was disabled since)
        var fixture = await FixtureAsync();

        // When
        var output = await fixture.Handler(null).HandleAsync(fixture.Command(CurrentTotpCode()));

        // Then
        Assert.False(output.Success);
        Assert.Contains(TwoFactorMessages.ChallengeTokenInvalid, output.Errors);
    }

    [UnitFact]
    public async Task GivenFactorValidButScopeNoLongerEligible_WhenHandlingVerify_ThenReturnsScopeNoLongerEligibleError()
    {
        // Given — UC-38 step 5: the app code is genuinely correct, but the person's scope was
        // logically deleted between UC-11's password check and this completion (the AF-11d
        // condition, re-checked here). Distinct from AF-38a: the challenge token and the factor
        // were both valid.
        var fixture = await FixtureAsync();
        var scope = new Scope { PublicId = Guid.NewGuid(), Name = "scope", IsDeleted = true };
        fixture.Person.RoleId = (long)Roles.User;
        fixture.Person.ScopeMembership = new ScopeUser { Scope = scope, Person = fixture.Person };
        await SeedActiveAsync(fixture, appEnabled: true, emailEnabled: false);

        // When
        var output = await fixture.Handler(null).HandleAsync(fixture.Command(CurrentTotpCode()));

        // Then
        Assert.False(output.Success);
        Assert.Contains(TwoFactorMessages.ScopeNoLongerEligible, output.Errors);
        Assert.DoesNotContain(TwoFactorMessages.ChallengeTokenInvalid, output.Errors);
        Assert.Null(fixture.TokenIssuer.Subject);
    }

    [UnitFact]
    public async Task GivenWrongAppCode_WhenHandlingVerify_ThenReturnsFactorInvalidError()
    {
        // Given — AF-38b
        var fixture = await FixtureAsync();
        await SeedActiveAsync(fixture, appEnabled: true, emailEnabled: false);

        // When
        var output = await fixture.Handler(null).HandleAsync(fixture.Command("000000"));

        // Then
        Assert.False(output.Success);
        Assert.Contains(TwoFactorMessages.FactorInvalid, output.Errors);
        Assert.Null(fixture.TokenIssuer.Subject);
    }

    [UnitFact]
    public async Task GivenNoCodeAndNoRecoveryCode_WhenHandlingVerify_ThenReturnsFactorInvalidError()
    {
        // Given — AF-38b: neither a code nor a recovery code submitted
        var fixture = await FixtureAsync();
        await SeedActiveAsync(fixture, appEnabled: true, emailEnabled: true);

        // When
        var output = await fixture.Handler(null).HandleAsync(fixture.Command());

        // Then
        Assert.False(output.Success);
        Assert.Contains(TwoFactorMessages.FactorInvalid, output.Errors);
    }

    [UnitFact]
    public async Task GivenUnknownRecoveryCode_WhenHandlingVerify_ThenReturnsFactorInvalidError()
    {
        // Given — AF-38b's recovery-code shape
        var fixture = await FixtureAsync();
        await SeedActiveAsync(fixture, appEnabled: true, emailEnabled: false);

        // When
        var output = await fixture.Handler(null).HandleAsync(fixture.Command(recoveryCode: "NEVER-ISSUED"));

        // Then
        Assert.False(output.Success);
        Assert.Contains(TwoFactorMessages.FactorInvalid, output.Errors);
    }

    [UnitFact]
    public async Task GivenAlreadyUsedRecoveryCode_WhenHandlingVerify_ThenReturnsSameFactorInvalidErrorAsWrongCode()
    {
        // Given — AF-38c: must answer identically to AF-38b
        var fixture = await FixtureAsync();
        var twoFactorAuth = await SeedActiveAsync(fixture, appEnabled: true, emailEnabled: false);
        const string plaintext = "CCCC-DDDD";
        await SeedRecoveryCodeAsync(fixture, twoFactorAuth.Id, plaintext, used: true);

        // When
        var output = await fixture.Handler(null).HandleAsync(fixture.Command(recoveryCode: plaintext));

        // Then
        Assert.False(output.Success);
        Assert.Contains(TwoFactorMessages.FactorInvalid, output.Errors);
        Assert.Null(fixture.TokenIssuer.Subject);
    }

    [UnitFact]
    public async Task GivenEmailEnabledAndExpiredEmailCode_WhenHandlingVerify_ThenReturnsFactorInvalidError()
    {
        // Given — AF-38b's email-code shape
        var fixture = await FixtureAsync();
        var twoFactorAuth = await SeedActiveAsync(fixture, appEnabled: false, emailEnabled: true);
        await SeedEmailCodeAsync(fixture, twoFactorAuth.Id, expired: true);

        // When
        var output = await fixture.Handler(null).HandleAsync(fixture.Command(EmailCode));

        // Then
        Assert.False(output.Success);
        Assert.Contains(TwoFactorMessages.FactorInvalid, output.Errors);
    }
}
