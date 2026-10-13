using ArturRios.Heimdall.Command.Handlers;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Services;
using ArturRios.Heimdall.Command.Tests.Support;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Mock;
using FluentValidation;
using FluentValidation.Results;
using Moq;

namespace ArturRios.Heimdall.Command.Tests;

// Unit tests for VerifyEmailCommandHandler (UC-14). Cover the main flow, AF-14a (expired), AF-14b
// (used), AF-14c (unknown), and the input validation NFR-10 requires, plus the three decisions the
// specification leaves open: every live token the person holds is consumed, an already-verified
// address verifies again idempotently, and a logically deleted person's address is still verified.
public class VerifyEmailCommandHandlerTests
{
    private static readonly DateTime Now = DateTime.UtcNow;

    private sealed record Fixture(
        AsyncFakeRepository<Person, long> Persons,
        AsyncFakeRepository<EmailVerificationToken, long> Tokens,
        Person Person)
    {
        public VerifyEmailCommandHandler Handler(
            IValidator<VerifyEmailCommand>? validator = null,
            IPendingRegistrationConfirmer? pendingRegistrations = null) =>
            new(validator ?? PassingValidator(), Tokens, Tokens, Persons, InMemoryAtomicWrites.Instance,
                pendingRegistrations ?? Confirming(PendingRegistrationOutcome.NotFound));
    }

    private sealed class StubConfirmer(PendingRegistrationConfirmation answer) : IPendingRegistrationConfirmer
    {
        public List<string> Presented { get; } = [];

        public Task<PendingRegistrationConfirmation> ConfirmAsync(string tokenHash, DateTime now)
        {
            Presented.Add(tokenHash);
            return Task.FromResult(answer);
        }
    }

    private static StubConfirmer Confirming(
        PendingRegistrationOutcome outcome, params string[] errors) => new(new(outcome, errors));

    private static IValidator<VerifyEmailCommand> PassingValidator()
    {
        var validator = new Mock<IValidator<VerifyEmailCommand>>();
        validator
            .Setup(v => v.ValidateAsync(It.IsAny<VerifyEmailCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidationResult());

        return validator.Object;
    }

    /// <summary>
    ///     Seeds one person and returns the repositories the handler is built from. Tokens are added
    ///     per test, since which ones exist is the subject.
    /// </summary>
    private static async Task<Fixture> FixtureAsync(bool isDeleted = false, bool emailVerified = false)
    {
        var persons = new AsyncFakeRepository<Person, long>();
        var person = new Person
        {
            PublicId = Guid.NewGuid(),
            Name = "person",
            Email = "person@test.local",
            RoleId = (long)Roles.SystemAdmin,
            EmailVerified = emailVerified,
            IsDeleted = isDeleted
        };

        await persons.CreateAsync(person); // assigns person.Id

        return new Fixture(persons, new AsyncFakeRepository<EmailVerificationToken, long>(), person);
    }

    /// <summary>
    ///     Adds a verification token for a person. The <c>Person</c> navigation is set explicitly
    ///     because <see cref="AsyncFakeRepository{T, TKey}" /> is an in-memory list and resolves no
    ///     <c>Include</c>.
    /// </summary>
    private static async Task<EmailVerificationToken> TokenForAsync(
        AsyncFakeRepository<EmailVerificationToken, long> tokens,
        Person person,
        string value,
        DateTime? expiresAt = null,
        bool used = false)
    {
        var token = new EmailVerificationToken
        {
            PersonId = person.Id,
            Person = person,
            TokenHash = SingleUseTokenHash.Of(value),
            ExpiresAt = expiresAt ?? Now.AddHours(1),
            Used = used
        };

        await tokens.CreateAsync(token);

        return token;
    }

    private static VerifyEmailCommand Command(string token) => new() { Token = token };

    [UnitFact]
    public async Task GivenLiveToken_WhenHandlingVerifyEmail_ThenEmailIsVerified()
    {
        // Given a person holding one unused, unexpired token
        var fixture = await FixtureAsync();
        await TokenForAsync(fixture.Tokens, fixture.Person, "live-token");

        // When
        var output = await fixture.Handler().HandleAsync(Command("live-token"));

        // Then — response
        Assert.True(output.Success);
        Assert.Empty(output.Errors);
        Assert.Contains(AuthMessages.EmailVerifiedSuccessfully, output.Messages);

        // Then — the flag the use case exists to set (FR-EV-03)
        Assert.True(fixture.Person.EmailVerified);
    }

    [UnitFact]
    public async Task GivenLiveToken_WhenHandlingVerifyEmail_ThenTokenIsConsumed()
    {
        // Given — UC-14 step 4
        var fixture = await FixtureAsync();
        var token = await TokenForAsync(fixture.Tokens, fixture.Person, "live-token");

        // When
        await fixture.Handler().HandleAsync(Command("live-token"));

        // Then
        Assert.True(token.Used);
    }

    [UnitFact]
    public async Task GivenLiveToken_WhenHandlingVerifyEmail_ThenUpdatedAtIsStamped()
    {
        // Given no database trigger maintains UpdatedAt — the handler does, as UC-08's and UC-13's do
        var fixture = await FixtureAsync();
        await TokenForAsync(fixture.Tokens, fixture.Person, "live-token");

        // When
        await fixture.Handler().HandleAsync(Command("live-token"));

        // Then
        Assert.True(fixture.Person.UpdatedAt >= Now);
    }

    [UnitFact]
    public async Task GivenSeveralLiveTokens_WhenHandlingVerifyEmail_ThenAllOfThemAreConsumed()
    {
        // Given someone issued a token at creation (UC-06) and another on request (UC-15), so two
        // links work. Once one has verified the address, the others verify an address that is already
        // verified — dead weight left live in a mailbox.
        var fixture = await FixtureAsync();
        var first = await TokenForAsync(fixture.Tokens, fixture.Person, "first-token");
        var second = await TokenForAsync(fixture.Tokens, fixture.Person, "second-token");

        // When the second link is the one clicked
        var output = await fixture.Handler().HandleAsync(Command("second-token"));

        // Then
        Assert.True(output.Success);
        Assert.True(second.Used);
        Assert.True(first.Used);
    }

    [UnitFact]
    public async Task GivenAnExpiredSiblingToken_WhenHandlingVerifyEmail_ThenItIsLeftAlone()
    {
        // Given a token already dead by AF-14a. Rewriting it would only make it report a different
        // reason for being dead.
        var fixture = await FixtureAsync();
        var expired = await TokenForAsync(
            fixture.Tokens, fixture.Person, "stale-token", Now.AddHours(-1));
        await TokenForAsync(fixture.Tokens, fixture.Person, "live-token");

        // When
        await fixture.Handler().HandleAsync(Command("live-token"));

        // Then
        Assert.False(expired.Used);
    }

    [UnitFact]
    public async Task GivenAnotherPersonHoldsALiveToken_WhenHandlingVerifyEmail_ThenTheirTokenSurvives()
    {
        // Given the boundary of the invalidation rule: it must reach the person's own tokens and stop
        var fixture = await FixtureAsync();
        var other = new Person { Email = "other@test.local", RoleId = (long)Roles.SystemAdmin };
        await fixture.Persons.CreateAsync(other);
        var theirToken = await TokenForAsync(fixture.Tokens, other, "their-token");
        await TokenForAsync(fixture.Tokens, fixture.Person, "live-token");

        // When
        await fixture.Handler().HandleAsync(Command("live-token"));

        // Then
        Assert.False(theirToken.Used);
        Assert.False(other.EmailVerified);
    }

    [UnitFact]
    public async Task GivenExpiredToken_WhenHandlingVerifyEmail_ThenReturnsTokenExpiredError()
    {
        // Given — AF-14a (FR-EV-02)
        var fixture = await FixtureAsync();
        await TokenForAsync(fixture.Tokens, fixture.Person, "stale-token", Now.AddSeconds(-1));

        // When
        var output = await fixture.Handler().HandleAsync(Command("stale-token"));

        // Then
        Assert.False(output.Success);
        Assert.Contains(AuthMessages.TokenExpired, output.Errors);
        Assert.False(fixture.Person.EmailVerified);
    }

    [UnitFact]
    public async Task GivenUsedToken_WhenHandlingVerifyEmail_ThenReturnsTokenAlreadyUsedError()
    {
        // Given — AF-14b: a link that has already verified an address cannot verify another
        var fixture = await FixtureAsync();
        await TokenForAsync(fixture.Tokens, fixture.Person, "spent-token", used: true);

        // When
        var output = await fixture.Handler().HandleAsync(Command("spent-token"));

        // Then
        Assert.False(output.Success);
        Assert.Contains(AuthMessages.TokenAlreadyUsed, output.Errors);
        Assert.False(fixture.Person.EmailVerified);
    }

    [UnitFact]
    public async Task GivenExpiredAndUsedToken_WhenHandlingVerifyEmail_ThenReturnsTokenExpiredError()
    {
        // Given both conditions at once. UC-14's main flow states the order — exists, not expired,
        // not used — so expiry is the reason reported.
        var fixture = await FixtureAsync();
        await TokenForAsync(fixture.Tokens, fixture.Person, "dead-token", Now.AddHours(-1), used: true);

        // When
        var output = await fixture.Handler().HandleAsync(Command("dead-token"));

        // Then
        Assert.Contains(AuthMessages.TokenExpired, output.Errors);
        Assert.DoesNotContain(AuthMessages.TokenAlreadyUsed, output.Errors);
    }

    [UnitFact]
    public async Task GivenUnknownToken_WhenHandlingVerifyEmail_ThenReturnsInvalidTokenError()
    {
        // Given — AF-14c: nothing matches what the caller presented
        var fixture = await FixtureAsync();
        await TokenForAsync(fixture.Tokens, fixture.Person, "live-token");

        // When
        var output = await fixture.Handler().HandleAsync(Command("some-other-token"));

        // Then
        Assert.False(output.Success);
        Assert.Contains(AuthMessages.TokenInvalid, output.Errors);
        Assert.False(fixture.Person.EmailVerified);
    }

    [UnitFact]
    public async Task GivenTokenDifferingOnlyInCase_WhenHandlingVerifyEmail_ThenReturnsInvalidTokenError()
    {
        // Given — AF-14c. Emails are matched case-insensitively across this system; a token is not.
        // It is a random secret, and folding its case would throw away part of its alphabet.
        var fixture = await FixtureAsync();
        await TokenForAsync(fixture.Tokens, fixture.Person, "LiveToken");

        // When
        var output = await fixture.Handler().HandleAsync(Command("livetoken"));

        // Then
        Assert.Contains(AuthMessages.TokenInvalid, output.Errors);
    }

    [UnitFact]
    public async Task GivenInvalidInput_WhenHandlingVerifyEmail_ThenReturnsValidationErrorAndChangesNothing()
    {
        // Given the validator rejects the command (NFR-10) — the lookup must not happen at all
        var fixture = await FixtureAsync();
        var token = await TokenForAsync(fixture.Tokens, fixture.Person, "live-token");
        var validator = new Mock<IValidator<VerifyEmailCommand>>();
        validator
            .Setup(v => v.ValidateAsync(It.IsAny<VerifyEmailCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidationResult([
                new ValidationFailure(nameof(VerifyEmailCommand.Token), AuthMessages.TokenRequired)
            ]));

        // When
        var output = await fixture.Handler(validator.Object).HandleAsync(Command(""));

        // Then
        Assert.False(output.Success);
        Assert.Contains(AuthMessages.TokenRequired, output.Errors);
        Assert.DoesNotContain(AuthMessages.EmailVerifiedSuccessfully, output.Messages);
        Assert.False(token.Used);
        Assert.False(fixture.Person.EmailVerified);
    }

    [UnitFact]
    public async Task GivenAlreadyVerifiedPerson_WhenHandlingVerifyEmail_ThenSucceedsAndConsumesTheToken()
    {
        // Given an address verified before this link was clicked. UC-14 defines no alternative flow
        // for it — AF-15a is about *requesting* another email, not about spending a token — so the
        // flag is set to the value it already holds and the token is spent.
        var fixture = await FixtureAsync(emailVerified: true);
        var token = await TokenForAsync(fixture.Tokens, fixture.Person, "live-token");

        // When
        var output = await fixture.Handler().HandleAsync(Command("live-token"));

        // Then
        Assert.True(output.Success);
        Assert.Contains(AuthMessages.EmailVerifiedSuccessfully, output.Messages);
        Assert.True(fixture.Person.EmailVerified);
        Assert.True(token.Used);
    }

    [UnitFact]
    public async Task GivenLogicallyDeletedPerson_WhenHandlingVerifyEmail_ThenEmailIsStillVerified()
    {
        // Given a deletion that landed between the email and the click. UC-14 defines no alternative
        // flow for it, and verifying grants nothing: UC-11 refuses the login by AF-11c.
        var fixture = await FixtureAsync(isDeleted: true);
        await TokenForAsync(fixture.Tokens, fixture.Person, "live-token");

        // When
        var output = await fixture.Handler().HandleAsync(Command("live-token"));

        // Then
        Assert.True(output.Success);
        Assert.True(fixture.Person.EmailVerified);
    }

    [UnitFact]
    public async Task GivenATokenUnknownToBothKinds_WhenHandlingVerifyEmail_ThenReturnsInvalidTokenError()
    {
        // Given — neither a verification token nor a pending registration matches
        var fixture = await FixtureAsync();
        var confirmer = Confirming(PendingRegistrationOutcome.NotFound);

        // When
        var output = await fixture.Handler(pendingRegistrations: confirmer).HandleAsync(Command("nobody-token"));

        // Then — the confirmer was asked by hash, and the answer is AF-14c
        Assert.False(output.Success);
        Assert.Contains(AuthMessages.TokenInvalid, output.Errors);
        Assert.Equal(SingleUseTokenHash.Of("nobody-token"), Assert.Single(confirmer.Presented));
    }

    [UnitFact]
    public async Task GivenAPendingRegistrationToken_WhenHandlingVerifyEmail_ThenReportsEmailVerified()
    {
        // Given a token that names no verification token but a pending registration the confirmer accepts
        var fixture = await FixtureAsync();

        // When
        var output = await fixture
            .Handler(pendingRegistrations: Confirming(PendingRegistrationOutcome.Confirmed))
            .HandleAsync(Command("pending-token"));

        // Then — the same wire answer a verification token gives
        Assert.True(output.Success);
        Assert.Contains(AuthMessages.EmailVerifiedSuccessfully, output.Messages);
        Assert.False(fixture.Person.EmailVerified);
    }

    [UnitFact]
    public async Task GivenAVerificationToken_WhenHandlingVerifyEmail_ThenThePendingRegistrationsAreNotConsulted()
    {
        // Given a live verification token
        var fixture = await FixtureAsync();
        await TokenForAsync(fixture.Tokens, fixture.Person, "live-token");
        var confirmer = Confirming(PendingRegistrationOutcome.Confirmed);

        // When
        await fixture.Handler(pendingRegistrations: confirmer).HandleAsync(Command("live-token"));

        // Then
        Assert.Empty(confirmer.Presented);
    }

    [UnitTheory]
    [InlineData(PendingRegistrationOutcome.Expired, "TokenExpired")]
    [InlineData(PendingRegistrationOutcome.AlreadyClaimed, "TokenAlreadyUsed")]
    [InlineData(PendingRegistrationOutcome.Refused, "TokenInvalid")]
    public async Task GivenAPendingOutcome_WhenHandlingVerifyEmail_ThenItMapsToTheMatchingTokenError(
        PendingRegistrationOutcome outcome, string message)
    {
        var fixture = await FixtureAsync();
        var expected = message switch
        {
            "TokenExpired" => AuthMessages.TokenExpired,
            "TokenAlreadyUsed" => AuthMessages.TokenAlreadyUsed,
            _ => AuthMessages.TokenInvalid
        };

        var output = await fixture.Handler(pendingRegistrations: Confirming(outcome))
            .HandleAsync(Command("pending-token"));

        Assert.False(output.Success);
        Assert.Contains(expected, output.Errors);
    }

    [UnitFact]
    public async Task GivenAPendingRegistrationThatFailsToStore_WhenHandlingVerifyEmail_ThenItsErrorsAreReturned()
    {
        var fixture = await FixtureAsync();

        var output = await fixture
            .Handler(pendingRegistrations: Confirming(PendingRegistrationOutcome.Failed, "store failed"))
            .HandleAsync(Command("pending-token"));

        Assert.False(output.Success);
        Assert.Contains("store failed", output.Errors);
    }
}
