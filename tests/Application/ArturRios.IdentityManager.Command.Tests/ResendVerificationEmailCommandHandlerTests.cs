using ArturRios.IdentityManager.Command.Handlers;
using ArturRios.IdentityManager.Command.Input;
using ArturRios.IdentityManager.Command.Services;
using ArturRios.IdentityManager.Domain.Entities;
using ArturRios.IdentityManager.Domain.Enums;
using ArturRios.IdentityManager.Shared.Messages;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Mock;
using Moq;

namespace ArturRios.IdentityManager.Command.Tests;

// Unit tests for ResendVerificationEmailCommandHandler (UC-15). Cover the main flow, AF-15a — the
// only alternative flow the specification defines — and the three decisions it leaves open: every
// outstanding live token is retired before a new one is issued, a bearer token naming no existing
// person answers "person not found", and a logically deleted person is still served.
//
// The send is the point of this use case, so IEmailVerificationService is a Moq mock that is verified
// rather than ignored: issuing the token is that service's job (UC-06 built it), and this handler's
// job is deciding whether and when to call it.
public class ResendVerificationEmailCommandHandlerTests
{
    private static readonly DateTime Now = DateTime.UtcNow;

    private sealed record Fixture(
        AsyncFakeRepository<Person> Persons,
        AsyncFakeRepository<EmailVerificationToken> Tokens,
        Mock<IEmailVerificationService> EmailVerification,
        Person Person)
    {
        public ResendVerificationEmailCommandHandler Handler() =>
            new(Persons, Tokens, Tokens, EmailVerification.Object);

        /// <summary>The command the controller would build for this fixture's person.</summary>
        public ResendVerificationEmailCommand Command() => new()
        {
            ActingPersonId = Person.PublicId, ActingRole = (int)Roles.SystemAdmin
        };
    }

    /// <summary>
    ///     Seeds one person and returns the repositories the handler is built from. Tokens are added
    ///     per test, since which ones exist is the subject.
    /// </summary>
    private static async Task<Fixture> FixtureAsync(bool isDeleted = false, bool emailVerified = false)
    {
        var persons = new AsyncFakeRepository<Person>();
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

        return new Fixture(
            persons,
            new AsyncFakeRepository<EmailVerificationToken>(),
            new Mock<IEmailVerificationService>(),
            person);
    }

    /// <summary>
    ///     Adds a verification token for a person. The <c>Person</c> navigation is set explicitly
    ///     because <see cref="AsyncFakeRepository{T}" /> is an in-memory list and resolves no
    ///     <c>Include</c>.
    /// </summary>
    private static async Task<EmailVerificationToken> TokenForAsync(
        AsyncFakeRepository<EmailVerificationToken> tokens,
        Person person,
        string value,
        DateTime? expiresAt = null,
        bool used = false)
    {
        var token = new EmailVerificationToken
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

    [UnitFact]
    public async Task GivenUnverifiedPerson_WhenHandlingResendVerificationEmail_ThenVerificationEmailIsIssuedAndSent()
    {
        // Given a person whose address is not yet verified, holding the token UC-06 issued
        var fixture = await FixtureAsync();
        await TokenForAsync(fixture.Tokens, fixture.Person, "original-token");

        // When
        var output = await fixture.Handler().HandleAsync(fixture.Command());

        // Then — response
        Assert.True(output.Success);
        Assert.Empty(output.Errors);
        Assert.Contains(AuthMessages.VerificationEmailSent, output.Messages);

        // Then — UC-15 steps 4 and 5 (FR-EV-04), delegated to the service UC-06 built
        fixture.EmailVerification.Verify(
            service => service.IssueAndSendAsync(fixture.Person), Times.Once);
    }

    [UnitFact]
    public async Task GivenUnverifiedPersonWithNoTokens_WhenHandlingResendVerificationEmail_ThenEmailIsStillSent()
    {
        // Given nothing to retire — the boundary of UC-15 step 3. A person can reach this state by
        // letting every link expire, and asking for a new one is exactly what they should then do.
        var fixture = await FixtureAsync();

        // When
        var output = await fixture.Handler().HandleAsync(fixture.Command());

        // Then
        Assert.True(output.Success);
        fixture.EmailVerification.Verify(
            service => service.IssueAndSendAsync(fixture.Person), Times.Once);
    }

    [UnitFact]
    public async Task GivenOutstandingLiveToken_WhenHandlingResendVerificationEmail_ThenItIsRetired()
    {
        // Given — UC-15 step 3. After a resend only the newest link works; otherwise "resend" would
        // mean "add another way in" rather than "replace the one you have".
        var fixture = await FixtureAsync();
        var outstanding = await TokenForAsync(fixture.Tokens, fixture.Person, "original-token");

        // When
        await fixture.Handler().HandleAsync(fixture.Command());

        // Then
        Assert.True(outstanding.Used);
    }

    [UnitFact]
    public async Task GivenTwoOutstandingLiveTokens_WhenHandlingResendVerificationEmail_ThenBothAreRetired()
    {
        // Given a person who has already resent once, so two links are live
        var fixture = await FixtureAsync();
        var first = await TokenForAsync(fixture.Tokens, fixture.Person, "first-token");
        var second = await TokenForAsync(fixture.Tokens, fixture.Person, "second-token");

        // When
        var output = await fixture.Handler().HandleAsync(fixture.Command());

        // Then
        Assert.True(output.Success);
        Assert.True(first.Used);
        Assert.True(second.Used);
    }

    [UnitFact]
    public async Task GivenAnExpiredToken_WhenHandlingResendVerificationEmail_ThenItIsLeftAlone()
    {
        // Given a token already dead by AF-14a. UC-14 draws the same boundary for the same reason:
        // rewriting it would only make a dead token report a different reason for being dead.
        var fixture = await FixtureAsync();
        var expired = await TokenForAsync(
            fixture.Tokens, fixture.Person, "stale-token", Now.AddHours(-1));

        // When
        await fixture.Handler().HandleAsync(fixture.Command());

        // Then
        Assert.False(expired.Used);
    }

    [UnitFact]
    public async Task GivenAnotherPersonHoldsALiveToken_WhenHandlingResendVerificationEmail_ThenTheirTokenSurvives()
    {
        // Given the boundary of the retirement rule: it must reach the caller's own tokens and stop
        var fixture = await FixtureAsync();
        var other = new Person { Email = "other@test.local", RoleId = (long)Roles.SystemAdmin };
        await fixture.Persons.CreateAsync(other);
        var theirToken = await TokenForAsync(fixture.Tokens, other, "their-token");
        await TokenForAsync(fixture.Tokens, fixture.Person, "original-token");

        // When
        await fixture.Handler().HandleAsync(fixture.Command());

        // Then
        Assert.False(theirToken.Used);
        fixture.EmailVerification.Verify(
            service => service.IssueAndSendAsync(other), Times.Never);
    }

    [UnitFact]
    public async Task GivenAlreadyVerifiedPerson_WhenHandlingResendVerificationEmail_ThenReturnsEmailAlreadyVerifiedError()
    {
        // Given — AF-15a: a link mailed to a verified address can do nothing when clicked
        var fixture = await FixtureAsync(emailVerified: true);

        // When
        var output = await fixture.Handler().HandleAsync(fixture.Command());

        // Then
        Assert.False(output.Success);
        Assert.Contains(AuthMessages.EmailAlreadyVerified, output.Errors);
    }

    [UnitFact]
    public async Task GivenAlreadyVerifiedPerson_WhenHandlingResendVerificationEmail_ThenNothingIsRetiredAndNothingIsSent()
    {
        // Given — AF-15a is checked before UC-15 step 3, so a refused request writes nothing
        var fixture = await FixtureAsync(emailVerified: true);
        var outstanding = await TokenForAsync(fixture.Tokens, fixture.Person, "original-token");

        // When
        await fixture.Handler().HandleAsync(fixture.Command());

        // Then
        Assert.False(outstanding.Used);
        fixture.EmailVerification.Verify(
            service => service.IssueAndSendAsync(It.IsAny<Person>()), Times.Never);
    }

    [UnitFact]
    public async Task GivenActorNamesNoExistingPerson_WhenHandlingResendVerificationEmail_ThenReturnsPersonNotFoundError()
    {
        // Given a valid bearer token that outlived the person it names. Authentication runs in
        // ClaimsOnly mode — no database read per request — so a hard deletion (UC-10) leaves the token
        // working and the person gone. There is no address to send to.
        var fixture = await FixtureAsync();

        // When
        var output = await fixture.Handler().HandleAsync(new ResendVerificationEmailCommand
        {
            ActingPersonId = Guid.NewGuid(), ActingRole = (int)Roles.SystemAdmin
        });

        // Then
        Assert.False(output.Success);
        Assert.Contains(AuthMessages.PersonNotFound, output.Errors);
        fixture.EmailVerification.Verify(
            service => service.IssueAndSendAsync(It.IsAny<Person>()), Times.Never);
    }

    [UnitFact]
    public async Task GivenLogicallyDeletedPerson_WhenHandlingResendVerificationEmail_ThenEmailIsStillSent()
    {
        // Given a deletion that landed after the caller's token was issued. UC-15 defines exactly one
        // alternative flow, so refusing here would be inventing a second — and verifying grants
        // nothing on its own, since UC-11 refuses their login by AF-11c either way.
        var fixture = await FixtureAsync(isDeleted: true);

        // When
        var output = await fixture.Handler().HandleAsync(fixture.Command());

        // Then
        Assert.True(output.Success);
        fixture.EmailVerification.Verify(
            service => service.IssueAndSendAsync(fixture.Person), Times.Once);
    }
}
