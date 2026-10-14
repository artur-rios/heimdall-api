using ArturRios.Heimdall.Command.Handlers;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Services;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Heimdall.Shared.Services;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Mock;
using Moq;

namespace ArturRios.Heimdall.Command.Tests;

// Unit tests for SendPersonVerificationEmailCommandHandler (UC-51, FR-EV-06/07): the main flow for a
// System Admin and an owning Scope Admin (live links retired, a fresh one issued through the same
// service UC-15 uses), AF-51a (not found), AF-51b (a non-owning Scope Admin, or a Scope Admin aiming at
// anyone but a User), AF-51c (deleted, restricted — restricted first), AF-51d (already verified), and
// AF-51e at both edges of the cooldown window, counting used tokens too.
public class SendPersonVerificationEmailCommandHandlerTests
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);
    private static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(5);

    /// <summary>The one scope the acting Scope Admin owns in these tests.</summary>
    private const long OwnedScopeId = 1;

    private sealed record Fixture(
        AsyncFakeRepository<Person, long> Persons,
        AsyncFakeRepository<EmailVerificationToken, long> Tokens,
        Mock<IScopeOwnershipChecker> Ownership,
        Mock<IEmailVerificationService> EmailVerification)
    {
        public SendPersonVerificationEmailCommandHandler Handler() => new(
            Persons,
            Tokens,
            Tokens,
            Ownership.Object,
            EmailVerification.Object,
            new EmailVerificationOptions { TokenLifetime = Lifetime },
            new AdminEmailOptions { Cooldown = Cooldown });
    }

    private static async Task<Fixture> FixtureWith(params Person[] persons)
    {
        var repository = new AsyncFakeRepository<Person, long>();

        foreach (var person in persons)
        {
            await repository.CreateAsync(person);
        }

        var ownership = new Mock<IScopeOwnershipChecker>();
        ownership
            .Setup(x => x.ActorMayManageScopeAsync(It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<long>()))
            .ReturnsAsync(false);
        ownership
            .Setup(x => x.ActorMayManageScopeAsync((int)Roles.ScopeAdmin, It.IsAny<Guid>(), OwnedScopeId))
            .ReturnsAsync(true);

        return new Fixture(
            repository,
            new AsyncFakeRepository<EmailVerificationToken, long>(),
            ownership,
            new Mock<IEmailVerificationService>());
    }

    private static Person User(
        long id, long scopeId = OwnedScopeId, bool emailVerified = false, bool isDeleted = false, bool restricted = false) => new()
    {
        Id = id,
        PublicId = Guid.NewGuid(),
        Name = $"user-{id}",
        Email = $"user-{id}@test.local",
        RoleId = (long)Roles.User,
        ScopeId = scopeId,
        EmailVerified = emailVerified,
        IsDeleted = isDeleted,
        ProcessingRestrictedAt = restricted ? DateTime.UtcNow : null
    };

    private static Person Administrator(long id, Roles role) => new()
    {
        Id = id,
        PublicId = Guid.NewGuid(),
        Name = $"admin-{id}",
        Email = $"admin-{id}@test.local",
        RoleId = (long)role
    };

    private static SendPersonVerificationEmailCommand Command(Guid id, Roles actingRole = Roles.SystemAdmin) => new()
    {
        Id = id,
        ActingPersonId = Guid.NewGuid(),
        ActingRole = (int)actingRole
    };

    /// <summary>Stores a verification token as if it had been issued <paramref name="ago" /> before now.</summary>
    private static async Task<EmailVerificationToken> TokenIssuedAsync(
        Fixture fixture, Person person, TimeSpan ago, bool used = false)
    {
        var token = new EmailVerificationToken
        {
            PersonId = person.Id,
            Person = person,
            TokenHash = SingleUseTokenHash.Of(Guid.NewGuid().ToString("N")),
            ExpiresAt = DateTime.UtcNow - ago + Lifetime,
            Used = used
        };

        await fixture.Tokens.CreateAsync(token);

        return token;
    }

    private static void NothingSent(Fixture fixture) =>
        fixture.EmailVerification.Verify(x => x.IssueAndSendAsync(It.IsAny<Person>()), Times.Never);

    [UnitFact]
    public async Task GivenASystemAdmin_WhenSending_ThenTheEmailIsIssuedForThePerson()
    {
        // Given an unverified User and no earlier link
        var target = User(10);
        var fixture = await FixtureWith(target);

        // When
        var output = await fixture.Handler().HandleAsync(Command(target.PublicId));

        // Then — the same service UC-15 uses, so the link uses the scope's EmailVerificationUrl
        Assert.True(output.Success);
        Assert.Contains(PersonMessages.VerificationEmailSentToPerson, output.Messages);
        Assert.Equal(target.PublicId, output.Data!.Id);
        fixture.EmailVerification.Verify(x => x.IssueAndSendAsync(target), Times.Once);
    }

    [UnitFact]
    public async Task GivenAScopeAdminOwningTheUsersScope_WhenSending_ThenTheEmailIsSent()
    {
        // Given a User of the scope the actor owns
        var target = User(10, scopeId: OwnedScopeId);
        var fixture = await FixtureWith(target);

        // When
        var output = await fixture.Handler().HandleAsync(Command(target.PublicId, Roles.ScopeAdmin));

        // Then
        Assert.True(output.Success);
        fixture.EmailVerification.Verify(x => x.IssueAndSendAsync(target), Times.Once);
    }

    [UnitFact]
    public async Task GivenAScopeAdminNotOwningTheUsersScope_WhenSending_ThenReturnsNotAuthorized()
    {
        // Given AF-51b — a User of scope 2
        var target = User(10, scopeId: 2);
        var fixture = await FixtureWith(target);

        // When
        var output = await fixture.Handler().HandleAsync(Command(target.PublicId, Roles.ScopeAdmin));

        // Then
        Assert.False(output.Success);
        Assert.Contains(PersonMessages.NotAuthorizedToEmailPerson, output.Errors);
        NothingSent(fixture);
    }

    [UnitFact]
    public async Task GivenAScopeAdminTargetingAnAdministrator_WhenSending_ThenReturnsNotAuthorized()
    {
        // Given AF-51b — a Scope Admin may act on Users only, whatever they co-own
        var target = Administrator(10, Roles.ScopeAdmin);
        var fixture = await FixtureWith(target);

        // When
        var output = await fixture.Handler().HandleAsync(Command(target.PublicId, Roles.ScopeAdmin));

        // Then
        Assert.Contains(PersonMessages.NotAuthorizedToEmailPerson, output.Errors);
        NothingSent(fixture);
    }

    [UnitFact]
    public async Task GivenAnUnknownPerson_WhenSending_ThenReturnsPersonNotFound()
    {
        // Given AF-51a — also how a Google User's id answers: they are not persons
        var fixture = await FixtureWith(User(10));

        // When
        var output = await fixture.Handler().HandleAsync(Command(Guid.NewGuid()));

        // Then
        Assert.False(output.Success);
        Assert.Contains(PersonMessages.PersonNotFound, output.Errors);
        NothingSent(fixture);
    }

    [UnitFact]
    public async Task GivenALogicallyDeletedPerson_WhenSending_ThenReturnsPersonIsDeleted()
    {
        // Given AF-51c
        var target = User(10, isDeleted: true);
        var fixture = await FixtureWith(target);

        // When
        var output = await fixture.Handler().HandleAsync(Command(target.PublicId));

        // Then
        Assert.Contains(PersonMessages.PersonIsDeleted, output.Errors);
        NothingSent(fixture);
    }

    [UnitFact]
    public async Task GivenARestrictedPerson_WhenSending_ThenReturnsPersonProcessingRestricted()
    {
        // Given AF-51c — sending would be processing a restricted identity (NFR-24)
        var target = User(10, restricted: true);
        var fixture = await FixtureWith(target);

        // When
        var output = await fixture.Handler().HandleAsync(Command(target.PublicId));

        // Then
        Assert.Contains(PersonMessages.PersonProcessingRestricted, output.Errors);
        NothingSent(fixture);
    }

    [UnitFact]
    public async Task GivenAPersonBothRestrictedAndDeleted_WhenSending_ThenTheRestrictionIsReported()
    {
        // Given both — the restriction is checked first, as for UC-52
        var target = User(10, isDeleted: true, restricted: true);
        var fixture = await FixtureWith(target);

        // When
        var output = await fixture.Handler().HandleAsync(Command(target.PublicId));

        // Then
        Assert.Contains(PersonMessages.PersonProcessingRestricted, output.Errors);
        Assert.DoesNotContain(PersonMessages.PersonIsDeleted, output.Errors);
    }

    [UnitFact]
    public async Task GivenAnOwningScopeAdminAndADeletedUser_WhenSending_ThenReturnsPersonIsDeleted()
    {
        // Given authorization passes before the state is examined — an owner may learn their own
        // User is deleted; nobody else learns anything (AF-51b answers first for them)
        var target = User(10, scopeId: OwnedScopeId, isDeleted: true);
        var fixture = await FixtureWith(target);

        // When
        var output = await fixture.Handler().HandleAsync(Command(target.PublicId, Roles.ScopeAdmin));

        // Then
        Assert.Contains(PersonMessages.PersonIsDeleted, output.Errors);
    }

    [UnitFact]
    public async Task GivenAVerifiedAddress_WhenSending_ThenReturnsEmailAlreadyVerified()
    {
        // Given AF-51d — the refusal UC-15 gives
        var target = User(10, emailVerified: true);
        var fixture = await FixtureWith(target);

        // When
        var output = await fixture.Handler().HandleAsync(Command(target.PublicId));

        // Then
        Assert.Contains(AuthMessages.EmailAlreadyVerified, output.Errors);
        NothingSent(fixture);
    }

    [UnitFact]
    public async Task GivenALinkIssuedJustInsideTheWindow_WhenSending_ThenReturnsAccountEmailSentRecently()
    {
        // Given AF-51e — a link issued 4m50s ago, with a five-minute cooldown
        var target = User(10);
        var fixture = await FixtureWith(target);
        var recent = await TokenIssuedAsync(fixture, target, TimeSpan.FromMinutes(4) + TimeSpan.FromSeconds(50));

        // When
        var output = await fixture.Handler().HandleAsync(Command(target.PublicId));

        // Then — nothing sent, and the outstanding link is left working
        Assert.False(output.Success);
        Assert.Contains(PersonMessages.AccountEmailSentRecently, output.Errors);
        NothingSent(fixture);
        Assert.False(recent.Used);
    }

    [UnitFact]
    public async Task GivenALinkIssuedJustOutsideTheWindow_WhenSending_ThenItIsRetiredAndAFreshOneSent()
    {
        // Given a live link issued 5m10s ago
        var target = User(10);
        var fixture = await FixtureWith(target);
        var older = await TokenIssuedAsync(fixture, target, TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(10));

        // When
        var output = await fixture.Handler().HandleAsync(Command(target.PublicId));

        // Then — as UC-15 does, only the new link will work
        Assert.True(output.Success);
        Assert.True(older.Used);
        fixture.EmailVerification.Verify(x => x.IssueAndSendAsync(target), Times.Once);
    }

    [UnitFact]
    public async Task GivenAUsedLinkInsideTheWindow_WhenSending_ThenTheCooldownStillRuns()
    {
        // Given a link issued a minute ago and already used or superseded — an email was still sent
        var target = User(10);
        var fixture = await FixtureWith(target);
        await TokenIssuedAsync(fixture, target, TimeSpan.FromMinutes(1), used: true);

        // When
        var output = await fixture.Handler().HandleAsync(Command(target.PublicId));

        // Then
        Assert.Contains(PersonMessages.AccountEmailSentRecently, output.Errors);
        NothingSent(fixture);
    }

    [UnitFact]
    public async Task GivenAnotherPersonsRecentLink_WhenSending_ThenTheCooldownDoesNotApply()
    {
        // Given the cooldown is per person
        var target = User(10);
        var other = User(11);
        var fixture = await FixtureWith(target, other);
        await TokenIssuedAsync(fixture, other, TimeSpan.FromMinutes(1));

        // When
        var output = await fixture.Handler().HandleAsync(Command(target.PublicId));

        // Then
        Assert.True(output.Success);
    }
}
