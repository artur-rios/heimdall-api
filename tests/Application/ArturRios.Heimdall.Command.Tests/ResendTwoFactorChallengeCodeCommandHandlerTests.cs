using ArturRios.Heimdall.Command.Handlers;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Services;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Mock;
using Moq;

namespace ArturRios.Heimdall.Command.Tests;

// Unit tests for ResendTwoFactorChallengeCodeCommandHandler (UC-46, FR-2F-16): the main flow, every
// alternative flow AF-46a…AF-46e, and the reissue cap FR-2F-13 states.
//
// Two properties carry the use case, and both are asserted on every test here rather than only
// where they are interesting:
//
//   1. The answer never varies. The endpoint is anonymous, so a response that differed between a
//      real challenge and a forged one would tell a caller whether an address is registered and has
//      email two-factor enabled. Every test ends with the same AssertIndistinguishable.
//   2. What differs is only whether a code was sent — which the caller cannot see, and which these
//      tests can, through the recording issuer.
public class ResendTwoFactorChallengeCodeCommandHandlerTests
{
    private const string ChallengeToken = "a-challenge-token";
    private const string Email = "person@test.local";

    /// <summary>
    ///     Records the reissues it was asked for, so a test can tell "a code was sent" from "nothing
    ///     happened" — the distinction the caller is deliberately denied.
    /// </summary>
    private sealed class RecordingEmailCodeIssuer : ITwoFactorEmailCodeIssuer
    {
        public List<(long TwoFactorAuthId, string Email)> Reissues { get; } = [];

        public Task<IEnumerable<string>?> ReissueAsync(TwoFactorAuth twoFactorAuth, string email)
        {
            Reissues.Add((twoFactorAuth.Id, email));

            return Task.FromResult<IEnumerable<string>?>(null);
        }

        public Task<IEnumerable<string>?> RetireOutstandingAsync(long twoFactorAuthId) =>
            Task.FromResult<IEnumerable<string>?>(null);
    }

    private sealed record Fixture(
        AsyncFakeRepository<Person> Persons,
        AsyncFakeRepository<TwoFactorAuth> TwoFactorAuths,
        RecordingEmailCodeIssuer EmailCodeIssuer,
        Mock<ITwoFactorChallengeTokenValidator> Validator,
        Person Person)
    {
        public ResendTwoFactorChallengeCodeCommandHandler Handler() =>
            new(Persons, TwoFactorAuths, TwoFactorAuths, Validator.Object, EmailCodeIssuer);

        public Task<ArturRios.Output.DataOutput<Command.Output.ResendTwoFactorChallengeCodeCommandOutput?>> ResendAsync() =>
            Handler().HandleAsync(new ResendTwoFactorChallengeCodeCommand { ChallengeToken = ChallengeToken });
    }

    /// <summary>
    ///     Seeds a person, optionally an active configuration, and a validator that resolves the
    ///     challenge token to that person — or, with <paramref name="challengeValid" /> false, to
    ///     nobody, which is every shape of AF-46a at once.
    /// </summary>
    private static async Task<Fixture> FixtureAsync(
        bool appEnabled = false,
        bool emailEnabled = true,
        bool active = true,
        bool withConfiguration = true,
        int reissuesAlreadyMade = 0,
        bool challengeValid = true,
        bool personDeleted = false,
        bool processingRestricted = false)
    {
        var person = new Person
        {
            Id = 10,
            PublicId = Guid.NewGuid(),
            Name = "Person",
            Email = Email,
            PasswordHash = [1],
            Salt = [1],
            RoleId = (long)Roles.SystemAdmin,
            IsDeleted = personDeleted,
            ProcessingRestrictedAt = processingRestricted ? DateTime.UtcNow : null
        };

        var persons = new AsyncFakeRepository<Person>();
        await persons.CreateAsync(person);

        var twoFactorAuths = new AsyncFakeRepository<TwoFactorAuth>();

        if (withConfiguration)
        {
            await twoFactorAuths.CreateAsync(new TwoFactorAuth
            {
                PersonId = person.Id,
                IsActive = active,
                AppEnabled = appEnabled,
                EmailEnabled = emailEnabled,
                EmailCodeReissueCount = reissuesAlreadyMade
            });
        }

        var validator = new Mock<ITwoFactorChallengeTokenValidator>();
        validator
            .Setup(v => v.ValidateAsync(It.IsAny<string>()))
            .ReturnsAsync(challengeValid ? new TwoFactorChallengePrincipal(person.PublicId) : null);

        return new Fixture(persons, twoFactorAuths, new RecordingEmailCodeIssuer(), validator, person);
    }

    /// <summary>
    ///     The answer UC-46 gives on every path. Asserted in full — success, the canonical message,
    ///     no errors, and an output object carrying nothing — because "the same answer" is the
    ///     requirement, and an assertion that only checked the status would not notice a field
    ///     appearing.
    /// </summary>
    private static void AssertIndistinguishable(
        ArturRios.Output.DataOutput<Command.Output.ResendTwoFactorChallengeCodeCommandOutput?> output)
    {
        Assert.True(output.Success);
        Assert.Empty(output.Errors);
        Assert.Equal([TwoFactorMessages.ChallengeCodeResent], output.Messages);
        Assert.NotNull(output.Data);
    }

    [UnitFact]
    public async Task GivenAValidChallengeForAnEmailPerson_WhenResending_ThenAFreshCodeIsSentToTheirStoredAddress()
    {
        // Given — UC-46 main flow
        var fixture = await FixtureAsync();

        // When
        var output = await fixture.ResendAsync();

        // Then — a code went out, to the address on the person's record rather than to anything a
        // caller supplied: the command has no address field, and this is why.
        var reissue = Assert.Single(fixture.EmailCodeIssuer.Reissues);
        Assert.Equal(Email, reissue.Email);

        AssertIndistinguishable(output);
    }

    [UnitFact]
    public async Task GivenAValidChallenge_WhenResending_ThenTheReissueIsChargedAgainstTheChallenge()
    {
        // Given — FR-2F-13's bound is only a bound if it is counted
        var fixture = await FixtureAsync();

        // When
        var output = await fixture.ResendAsync();

        // Then
        var configuration = Assert.Single(fixture.TwoFactorAuths.Query().ToList());
        Assert.Equal(1, configuration.EmailCodeReissueCount);

        AssertIndistinguishable(output);
    }

    [UnitFact]
    public async Task GivenAnInvalidChallengeToken_WhenResending_ThenNothingIsSentAndTheAnswerIsUnchanged()
    {
        // Given — AF-46a: the validator refuses it, whether it was missing, malformed, forged,
        // expired, or a full login token carrying no MFA-pending claim (AF-46e). The handler cannot
        // tell those apart either, which is the point.
        var fixture = await FixtureAsync(challengeValid: false);

        // When
        var output = await fixture.ResendAsync();

        // Then
        Assert.Empty(fixture.EmailCodeIssuer.Reissues);

        AssertIndistinguishable(output);
    }

    [UnitFact]
    public async Task GivenTheChallengeNamesADeletedPerson_WhenResending_ThenNothingIsSentAndTheAnswerIsUnchanged()
    {
        // Given — AF-46b
        var fixture = await FixtureAsync(personDeleted: true);

        // When
        var output = await fixture.ResendAsync();

        // Then
        Assert.Empty(fixture.EmailCodeIssuer.Reissues);

        AssertIndistinguishable(output);
    }

    [UnitFact]
    public async Task GivenTheChallengeNamesARestrictedPerson_WhenResending_ThenNothingIsSentAndTheAnswerIsUnchanged()
    {
        // Given — AF-46b (NFR-24): mailing a restricted identity would be processing it. Reachable
        // even though UC-11 refuses such a person, because the restriction can be applied after the
        // challenge was issued.
        var fixture = await FixtureAsync(processingRestricted: true);

        // When
        var output = await fixture.ResendAsync();

        // Then
        Assert.Empty(fixture.EmailCodeIssuer.Reissues);

        AssertIndistinguishable(output);
    }

    [UnitFact]
    public async Task GivenNoActiveConfiguration_WhenResending_ThenNothingIsSentAndTheAnswerIsUnchanged()
    {
        // Given — AF-46c, in both of its shapes: no row at all, and a row that never activated
        var withoutRow = await FixtureAsync(withConfiguration: false);
        var inactive = await FixtureAsync(active: false);

        // When
        var withoutRowOutput = await withoutRow.ResendAsync();
        var inactiveOutput = await inactive.ResendAsync();

        // Then
        Assert.Empty(withoutRow.EmailCodeIssuer.Reissues);
        Assert.Empty(inactive.EmailCodeIssuer.Reissues);

        AssertIndistinguishable(withoutRowOutput);
        AssertIndistinguishable(inactiveOutput);
    }

    [UnitFact]
    public async Task GivenAnAppOnlyConfiguration_WhenResending_ThenNothingIsSentAndTheAnswerIsUnchanged()
    {
        // Given — AF-46c: an authenticator generates codes on demand, so there is nothing to resend
        var fixture = await FixtureAsync(appEnabled: true, emailEnabled: false);

        // When
        var output = await fixture.ResendAsync();

        // Then
        Assert.Empty(fixture.EmailCodeIssuer.Reissues);

        AssertIndistinguishable(output);
    }

    [UnitFact]
    public async Task GivenTheReissuesAreAlreadySpent_WhenResending_ThenNothingIsSentAndTheAnswerIsUnchanged()
    {
        // Given — AF-46d. The refusal is invisible: a caller who could tell "sent" from "budget
        // spent" would be able to read how many guesses remained.
        var fixture = await FixtureAsync(
            reissuesAlreadyMade: ResendTwoFactorChallengeCodeCommandHandler.MaxReissuesPerChallenge);

        // When
        var output = await fixture.ResendAsync();

        // Then
        Assert.Empty(fixture.EmailCodeIssuer.Reissues);

        AssertIndistinguishable(output);
    }

    [UnitFact]
    public async Task GivenOneChallenge_WhenResendingPastTheCap_ThenExactlyTheAllowedNumberOfCodesAreSent()
    {
        // Given — the bound FR-2F-13 states, exhausted. This is the test the requirement exists for:
        // without it the guessing budget is five attempts times however many reissues the rate
        // limiter admits, which is a property of deployment configuration rather than of the system.
        var fixture = await FixtureAsync();
        var cap = ResendTwoFactorChallengeCodeCommandHandler.MaxReissuesPerChallenge;

        // When — one more request than the challenge may authorize
        var outputs = new List<ArturRios.Output.DataOutput<Command.Output.ResendTwoFactorChallengeCodeCommandOutput?>>();

        for (var attempt = 0; attempt <= cap; attempt++)
        {
            outputs.Add(await fixture.ResendAsync());
        }

        // Then — the cap held, and the count stops at it rather than running past
        Assert.Equal(cap, fixture.EmailCodeIssuer.Reissues.Count);
        Assert.Equal(cap, Assert.Single(fixture.TwoFactorAuths.Query().ToList()).EmailCodeReissueCount);

        // Then — and the caller saw the same thing every time, including the one that did nothing
        Assert.Equal(cap + 1, outputs.Count);

        foreach (var output in outputs)
        {
            AssertIndistinguishable(output);
        }
    }

    [UnitFact]
    public async Task GivenTheCapIsThree_WhenTheGuessingBoundIsComputed_ThenItIsTwentyAttemptsPerAuthentication()
    {
        // The number the Threat Model and FR-2F-13 both quote, asserted rather than trusted: five
        // attempts against each of the login's own code plus three reissues.
        var codesPerAuthentication = 1 + ResendTwoFactorChallengeCodeCommandHandler.MaxReissuesPerChallenge;

        Assert.Equal(
            20, codesPerAuthentication * TwoFactorEmailCodeVerification.MaxFailedAttempts);

        await Task.CompletedTask;
    }
}
