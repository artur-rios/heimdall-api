using ArturRios.IdentityManager.Command.Handlers;
using ArturRios.IdentityManager.Command.Input;
using ArturRios.IdentityManager.Domain.Entities;
using ArturRios.IdentityManager.Domain.Enums;
using ArturRios.IdentityManager.Shared.Messages;
using ArturRios.Util.Hashing;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Mock;
using FluentValidation;
using FluentValidation.Results;
using Moq;

namespace ArturRios.IdentityManager.Command.Tests;

// Unit tests for ResetPasswordCommandHandler (UC-13). Cover the main flow, AF-13a (expired),
// AF-13b (used), AF-13c (unknown), and AF-13d (invalid input), plus the two decisions the
// specification leaves open: every live token the person holds is consumed, and a logically deleted
// person's password is still replaced.
//
// The password is only ever observable through Hash.TextMatches, so that — not the byte array — is
// what these assert: the new password verifies, the old one no longer does.
public class ResetPasswordCommandHandlerTests
{
    private const string OldPassword = "0ld-Pa55word!";
    private const string NewPassword = "Str0ng-New-Pass!";

    private static readonly DateTime Now = DateTime.UtcNow;

    private sealed record Fixture(
        AsyncFakeRepository<Person> Persons,
        AsyncFakeRepository<PasswordResetToken> Tokens,
        Person Person)
    {
        public ResetPasswordCommandHandler Handler(IValidator<ResetPasswordCommand>? validator = null) =>
            new(validator ?? PassingValidator(), Tokens, Tokens, Persons);
    }

    private static IValidator<ResetPasswordCommand> PassingValidator()
    {
        var validator = new Mock<IValidator<ResetPasswordCommand>>();
        validator
            .Setup(v => v.ValidateAsync(It.IsAny<ResetPasswordCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidationResult());

        return validator.Object;
    }

    /// <summary>
    ///     Seeds one person holding <paramref name="password" />, and returns the repositories the
    ///     handler is built from. Tokens are added per test, since which ones exist is the subject.
    /// </summary>
    private static async Task<Fixture> FixtureAsync(bool isDeleted = false, string password = OldPassword)
    {
        var persons = new AsyncFakeRepository<Person>();
        var person = new Person
        {
            PublicId = Guid.NewGuid(),
            Name = "person",
            Email = "person@test.local",
            PasswordHash = Hash.EncodeWithRandomSalt(password, out var salt),
            Salt = salt,
            RoleId = (long)Roles.SystemAdmin,
            IsDeleted = isDeleted
        };

        await persons.CreateAsync(person); // assigns person.Id

        return new Fixture(persons, new AsyncFakeRepository<PasswordResetToken>(), person);
    }

    /// <summary>
    ///     Adds a token for a person. The <c>Person</c> navigation is set explicitly because
    ///     <see cref="AsyncFakeRepository{T}" /> is an in-memory list and resolves no <c>Include</c>.
    /// </summary>
    private static async Task<PasswordResetToken> TokenForAsync(
        AsyncFakeRepository<PasswordResetToken> tokens,
        Person person,
        string value,
        DateTime? expiresAt = null,
        bool used = false)
    {
        var token = new PasswordResetToken
        {
            PersonId = person.Id,
            Person = person,
            Token = value,
            ExpiresAt = expiresAt ?? Now.AddHours(1),
            Used = used
        };

        await tokens.CreateAsync(token);

        return token;
    }

    private static ResetPasswordCommand Command(string token, string password = NewPassword) =>
        new() { Token = token, NewPassword = password };

    [UnitFact]
    public async Task GivenLiveToken_WhenHandlingResetPassword_ThenPasswordIsReplaced()
    {
        // Given a person holding one unused, unexpired token
        var fixture = await FixtureAsync();
        await TokenForAsync(fixture.Tokens, fixture.Person, "live-token");

        // When
        var output = await fixture.Handler().HandleAsync(Command("live-token"));

        // Then — response
        Assert.True(output.Success);
        Assert.Empty(output.Errors);
        Assert.Contains(AuthMessages.PasswordResetSuccessful, output.Messages);

        // Then — the new password verifies and the old one no longer does (FR-PR-03)
        Assert.True(Hash.TextMatches(NewPassword, fixture.Person.PasswordHash, fixture.Person.Salt));
        Assert.False(Hash.TextMatches(OldPassword, fixture.Person.PasswordHash, fixture.Person.Salt));
    }

    [UnitFact]
    public async Task GivenLiveToken_WhenHandlingResetPassword_ThenSaltIsRegenerated()
    {
        // Given — UC-13 step 3 asks for a new random salt, not a re-use of the person's existing one
        var fixture = await FixtureAsync();
        var originalSalt = fixture.Person.Salt.ToArray();
        await TokenForAsync(fixture.Tokens, fixture.Person, "live-token");

        // When
        await fixture.Handler().HandleAsync(Command("live-token"));

        // Then
        Assert.NotEqual(originalSalt, fixture.Person.Salt);
    }

    [UnitFact]
    public async Task GivenLiveToken_WhenHandlingResetPassword_ThenUpdatedAtIsStamped()
    {
        // Given no database trigger maintains UpdatedAt — the handler does, as UC-08's does
        var fixture = await FixtureAsync();
        await TokenForAsync(fixture.Tokens, fixture.Person, "live-token");

        // When
        await fixture.Handler().HandleAsync(Command("live-token"));

        // Then
        Assert.True(fixture.Person.UpdatedAt >= Now);
    }

    [UnitFact]
    public async Task GivenLiveToken_WhenHandlingResetPassword_ThenTokenIsConsumed()
    {
        // Given — UC-13 step 4
        var fixture = await FixtureAsync();
        var token = await TokenForAsync(fixture.Tokens, fixture.Person, "live-token");

        // When
        await fixture.Handler().HandleAsync(Command("live-token"));

        // Then
        Assert.True(token.Used);
    }

    [UnitFact]
    public async Task GivenSeveralLiveTokens_WhenHandlingResetPassword_ThenAllOfThemAreConsumed()
    {
        // Given someone who clicked "forgot password" twice, so UC-12 issued two working links. Once
        // one has changed the password, the other is a second chance to change it from a mailbox
        // that may be the reason the reset was needed.
        var fixture = await FixtureAsync();
        var used = await TokenForAsync(fixture.Tokens, fixture.Person, "first-token");
        var sibling = await TokenForAsync(fixture.Tokens, fixture.Person, "second-token");

        // When the second link is the one clicked
        var output = await fixture.Handler().HandleAsync(Command("second-token"));

        // Then
        Assert.True(output.Success);
        Assert.True(sibling.Used);
        Assert.True(used.Used);
    }

    [UnitFact]
    public async Task GivenAnExpiredSiblingToken_WhenHandlingResetPassword_ThenItIsLeftAlone()
    {
        // Given a token already dead by AF-13a. Rewriting it would only make it report a different
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
    public async Task GivenAnotherPersonHoldsALiveToken_WhenHandlingResetPassword_ThenTheirTokenSurvives()
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
    }

    [UnitFact]
    public async Task GivenExpiredToken_WhenHandlingResetPassword_ThenReturnsTokenExpiredError()
    {
        // Given — AF-13a (FR-PR-04)
        var fixture = await FixtureAsync();
        await TokenForAsync(fixture.Tokens, fixture.Person, "stale-token", Now.AddSeconds(-1));

        // When
        var output = await fixture.Handler().HandleAsync(Command("stale-token"));

        // Then
        Assert.False(output.Success);
        Assert.Contains(AuthMessages.ResetTokenExpired, output.Errors);
        Assert.True(Hash.TextMatches(OldPassword, fixture.Person.PasswordHash, fixture.Person.Salt));
    }

    [UnitFact]
    public async Task GivenUsedToken_WhenHandlingResetPassword_ThenReturnsTokenAlreadyUsedError()
    {
        // Given — AF-13b: a link that has already changed a password cannot change another
        var fixture = await FixtureAsync();
        await TokenForAsync(fixture.Tokens, fixture.Person, "spent-token", used: true);

        // When
        var output = await fixture.Handler().HandleAsync(Command("spent-token"));

        // Then
        Assert.False(output.Success);
        Assert.Contains(AuthMessages.ResetTokenAlreadyUsed, output.Errors);
        Assert.True(Hash.TextMatches(OldPassword, fixture.Person.PasswordHash, fixture.Person.Salt));
    }

    [UnitFact]
    public async Task GivenExpiredAndUsedToken_WhenHandlingResetPassword_ThenReturnsTokenExpiredError()
    {
        // Given both conditions at once. UC-13's main flow states the order — exists, not expired,
        // not used — so expiry is the reason reported.
        var fixture = await FixtureAsync();
        await TokenForAsync(fixture.Tokens, fixture.Person, "dead-token", Now.AddHours(-1), used: true);

        // When
        var output = await fixture.Handler().HandleAsync(Command("dead-token"));

        // Then
        Assert.Contains(AuthMessages.ResetTokenExpired, output.Errors);
        Assert.DoesNotContain(AuthMessages.ResetTokenAlreadyUsed, output.Errors);
    }

    [UnitFact]
    public async Task GivenUnknownToken_WhenHandlingResetPassword_ThenReturnsInvalidTokenError()
    {
        // Given — AF-13c: nothing matches what the caller presented
        var fixture = await FixtureAsync();
        await TokenForAsync(fixture.Tokens, fixture.Person, "live-token");

        // When
        var output = await fixture.Handler().HandleAsync(Command("some-other-token"));

        // Then
        Assert.False(output.Success);
        Assert.Contains(AuthMessages.ResetTokenInvalid, output.Errors);
        Assert.True(Hash.TextMatches(OldPassword, fixture.Person.PasswordHash, fixture.Person.Salt));
    }

    [UnitFact]
    public async Task GivenTokenDifferingOnlyInCase_WhenHandlingResetPassword_ThenReturnsInvalidTokenError()
    {
        // Given — AF-13c. Emails are matched case-insensitively across this system; a token is not.
        // It is a random secret, and folding its case would throw away part of its alphabet.
        var fixture = await FixtureAsync();
        await TokenForAsync(fixture.Tokens, fixture.Person, "LiveToken");

        // When
        var output = await fixture.Handler().HandleAsync(Command("livetoken"));

        // Then
        Assert.Contains(AuthMessages.ResetTokenInvalid, output.Errors);
    }

    [UnitFact]
    public async Task GivenInvalidInput_WhenHandlingResetPassword_ThenReturnsValidationErrorAndChangesNothing()
    {
        // Given the validator rejects the command (AF-13d) — the lookup must not happen at all
        var fixture = await FixtureAsync();
        var token = await TokenForAsync(fixture.Tokens, fixture.Person, "live-token");
        var validator = new Mock<IValidator<ResetPasswordCommand>>();
        validator
            .Setup(v => v.ValidateAsync(It.IsAny<ResetPasswordCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidationResult([
                new ValidationFailure(nameof(ResetPasswordCommand.NewPassword), AuthMessages.PasswordTooShort)
            ]));

        // When
        var output = await fixture.Handler(validator.Object).HandleAsync(Command("live-token", "short"));

        // Then
        Assert.False(output.Success);
        Assert.Contains(AuthMessages.PasswordTooShort, output.Errors);
        Assert.DoesNotContain(AuthMessages.PasswordResetSuccessful, output.Messages);
        Assert.False(token.Used);
        Assert.True(Hash.TextMatches(OldPassword, fixture.Person.PasswordHash, fixture.Person.Salt));
    }

    [UnitFact]
    public async Task GivenLogicallyDeletedPerson_WhenHandlingResetPassword_ThenPasswordIsStillReplaced()
    {
        // Given a deletion that landed between the email and the click. UC-13 defines no alternative
        // flow for it, and the new password grants nothing: UC-11 refuses the login by AF-11c.
        var fixture = await FixtureAsync(isDeleted: true);
        await TokenForAsync(fixture.Tokens, fixture.Person, "live-token");

        // When
        var output = await fixture.Handler().HandleAsync(Command("live-token"));

        // Then
        Assert.True(output.Success);
        Assert.True(Hash.TextMatches(NewPassword, fixture.Person.PasswordHash, fixture.Person.Salt));
    }
}
