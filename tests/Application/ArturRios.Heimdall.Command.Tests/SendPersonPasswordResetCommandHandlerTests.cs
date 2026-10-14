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

// Unit tests for SendPersonPasswordResetCommandHandler (UC-52, FR-PR-05/06): the main flow for a System
// Admin and an owning Scope Admin (sent directly, not through UC-12's anonymous queue, and earlier live
// links left as UC-12 leaves them), AF-52a, AF-52b, AF-52c (restricted, deleted), AF-52d (UC-12's
// "could not sign in": a User whose scope is deleted, a Scope Admin with no live scope), and AF-52e at
// both edges of the cooldown, counting used tokens too.
public class SendPersonPasswordResetCommandHandlerTests
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);
    private static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(5);

    /// <summary>The one scope the acting Scope Admin owns in these tests.</summary>
    private const long OwnedScopeId = 1;

    private sealed record Fixture(
        AsyncFakeRepository<Person, long> Persons,
        AsyncFakeRepository<PasswordResetToken, long> Tokens,
        Mock<IScopeOwnershipChecker> Ownership,
        Mock<IPasswordResetService> PasswordReset)
    {
        public SendPersonPasswordResetCommandHandler Handler() => new(
            Persons,
            Tokens,
            Ownership.Object,
            PasswordReset.Object,
            new PasswordResetOptions { TokenLifetime = Lifetime },
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
            new AsyncFakeRepository<PasswordResetToken, long>(),
            ownership,
            new Mock<IPasswordResetService>());
    }

    private static Scope Scope(long id, bool isDeleted = false) =>
        new() { Id = id, PublicId = Guid.NewGuid(), Name = $"scope-{id}", IsDeleted = isDeleted };

    private static Person User(long id, Scope scope, bool isDeleted = false, bool restricted = false) => new()
    {
        Id = id,
        PublicId = Guid.NewGuid(),
        Name = $"user-{id}",
        Email = $"user-{id}@test.local",
        RoleId = (long)Roles.User,
        ScopeId = scope.Id,
        ScopeMembership = new ScopeUser { ScopeId = scope.Id, Scope = scope },
        IsDeleted = isDeleted,
        ProcessingRestrictedAt = restricted ? DateTime.UtcNow : null
    };

    private static Person ScopeAdmin(long id, params Scope[] owned) => new()
    {
        Id = id,
        PublicId = Guid.NewGuid(),
        Name = $"admin-{id}",
        Email = $"admin-{id}@test.local",
        RoleId = (long)Roles.ScopeAdmin,
        ScopeOwnerships = owned.Select(scope => new ScopeOwner { ScopeId = scope.Id, Scope = scope }).ToList()
    };

    private static SendPersonPasswordResetCommand Command(Guid id, Roles actingRole = Roles.SystemAdmin) => new()
    {
        Id = id,
        ActingPersonId = Guid.NewGuid(),
        ActingRole = (int)actingRole
    };

    /// <summary>Stores a reset token as if it had been issued <paramref name="ago" /> before now.</summary>
    private static async Task<PasswordResetToken> TokenIssuedAsync(
        Fixture fixture, Person person, TimeSpan ago, bool used = false)
    {
        var token = new PasswordResetToken
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
        fixture.PasswordReset.Verify(x => x.IssueAndSendAsync(It.IsAny<Person>()), Times.Never);

    [UnitFact]
    public async Task GivenASystemAdmin_WhenSending_ThenTheResetIsIssuedForThePerson()
    {
        // Given a User of a live scope
        var target = User(10, Scope(OwnedScopeId));
        var fixture = await FixtureWith(target);

        // When
        var output = await fixture.Handler().HandleAsync(Command(target.PublicId));

        // Then — directly, through the service UC-12's processor uses
        Assert.True(output.Success);
        Assert.Contains(PersonMessages.PasswordResetSentToPerson, output.Messages);
        Assert.Equal(target.PublicId, output.Data!.Id);
        fixture.PasswordReset.Verify(x => x.IssueAndSendAsync(target), Times.Once);
    }

    [UnitFact]
    public async Task GivenAScopeAdminOwningTheUsersScope_WhenSending_ThenTheResetIsSent()
    {
        // Given
        var target = User(10, Scope(OwnedScopeId));
        var fixture = await FixtureWith(target);

        // When
        var output = await fixture.Handler().HandleAsync(Command(target.PublicId, Roles.ScopeAdmin));

        // Then
        Assert.True(output.Success);
        fixture.PasswordReset.Verify(x => x.IssueAndSendAsync(target), Times.Once);
    }

    [UnitFact]
    public async Task GivenAScopeAdminNotOwningTheUsersScope_WhenSending_ThenReturnsNotAuthorized()
    {
        // Given AF-52b
        var target = User(10, Scope(2));
        var fixture = await FixtureWith(target);

        // When
        var output = await fixture.Handler().HandleAsync(Command(target.PublicId, Roles.ScopeAdmin));

        // Then
        Assert.Contains(PersonMessages.NotAuthorizedToEmailPerson, output.Errors);
        NothingSent(fixture);
    }

    [UnitFact]
    public async Task GivenAScopeAdminTargetingAnAdministrator_WhenSending_ThenReturnsNotAuthorized()
    {
        // Given AF-52b
        var target = ScopeAdmin(10, Scope(OwnedScopeId));
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
        // Given AF-52a
        var fixture = await FixtureWith(User(10, Scope(OwnedScopeId)));

        // When
        var output = await fixture.Handler().HandleAsync(Command(Guid.NewGuid()));

        // Then
        Assert.Contains(PersonMessages.PersonNotFound, output.Errors);
        NothingSent(fixture);
    }

    [UnitFact]
    public async Task GivenARestrictedPerson_WhenSending_ThenReturnsPersonProcessingRestricted()
    {
        // Given AF-52c (NFR-24)
        var target = User(10, Scope(OwnedScopeId), restricted: true);
        var fixture = await FixtureWith(target);

        // When
        var output = await fixture.Handler().HandleAsync(Command(target.PublicId));

        // Then
        Assert.Contains(PersonMessages.PersonProcessingRestricted, output.Errors);
        NothingSent(fixture);
    }

    [UnitFact]
    public async Task GivenALogicallyDeletedPerson_WhenSending_ThenReturnsPersonIsDeleted()
    {
        // Given AF-52c
        var target = User(10, Scope(OwnedScopeId), isDeleted: true);
        var fixture = await FixtureWith(target);

        // When
        var output = await fixture.Handler().HandleAsync(Command(target.PublicId));

        // Then
        Assert.Contains(PersonMessages.PersonIsDeleted, output.Errors);
        NothingSent(fixture);
    }

    [UnitFact]
    public async Task GivenAUserWhoseScopeIsDeleted_WhenSending_ThenReturnsPersonCannotSignIn()
    {
        // Given AF-52d — FR-AU-06 would refuse the login whatever the password
        var target = User(10, Scope(OwnedScopeId, isDeleted: true));
        var fixture = await FixtureWith(target);

        // When
        var output = await fixture.Handler().HandleAsync(Command(target.PublicId));

        // Then
        Assert.Contains(PersonMessages.PersonCannotSignIn, output.Errors);
        NothingSent(fixture);
    }

    [UnitFact]
    public async Task GivenAScopeAdminWithNoLiveScope_WhenSending_ThenReturnsPersonCannotSignIn()
    {
        // Given AF-52d — FR-AU-07
        var target = ScopeAdmin(10, Scope(5, isDeleted: true));
        var fixture = await FixtureWith(target);

        // When
        var output = await fixture.Handler().HandleAsync(Command(target.PublicId));

        // Then
        Assert.Contains(PersonMessages.PersonCannotSignIn, output.Errors);
        NothingSent(fixture);
    }

    [UnitFact]
    public async Task GivenAResetIssuedJustInsideTheWindow_WhenSending_ThenReturnsAccountEmailSentRecently()
    {
        // Given AF-52e — a reset issued 4m50s ago, with a five-minute cooldown
        var target = User(10, Scope(OwnedScopeId));
        var fixture = await FixtureWith(target);
        await TokenIssuedAsync(fixture, target, TimeSpan.FromMinutes(4) + TimeSpan.FromSeconds(50));

        // When
        var output = await fixture.Handler().HandleAsync(Command(target.PublicId));

        // Then
        Assert.Contains(PersonMessages.AccountEmailSentRecently, output.Errors);
        NothingSent(fixture);
    }

    [UnitFact]
    public async Task GivenAResetIssuedJustOutsideTheWindow_WhenSending_ThenItIsSentAndTheOlderLinkIsLeftAlone()
    {
        // Given a live reset issued 5m10s ago
        var target = User(10, Scope(OwnedScopeId));
        var fixture = await FixtureWith(target);
        var older = await TokenIssuedAsync(fixture, target, TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(10));

        // When
        var output = await fixture.Handler().HandleAsync(Command(target.PublicId));

        // Then — existing live reset tokens are left as IssueAndSendAsync leaves them for UC-12
        Assert.True(output.Success);
        Assert.False(older.Used);
        fixture.PasswordReset.Verify(x => x.IssueAndSendAsync(target), Times.Once);
    }

    [UnitFact]
    public async Task GivenAUsedResetInsideTheWindow_WhenSending_ThenTheCooldownStillRuns()
    {
        // Given a reset issued a minute ago and already used
        var target = User(10, Scope(OwnedScopeId));
        var fixture = await FixtureWith(target);
        await TokenIssuedAsync(fixture, target, TimeSpan.FromMinutes(1), used: true);

        // When
        var output = await fixture.Handler().HandleAsync(Command(target.PublicId));

        // Then
        Assert.Contains(PersonMessages.AccountEmailSentRecently, output.Errors);
        NothingSent(fixture);
    }

    [UnitFact]
    public async Task GivenAnotherPersonsRecentReset_WhenSending_ThenTheCooldownDoesNotApply()
    {
        // Given the cooldown is per person
        var scope = Scope(OwnedScopeId);
        var target = User(10, scope);
        var other = User(11, scope);
        var fixture = await FixtureWith(target, other);
        await TokenIssuedAsync(fixture, other, TimeSpan.FromMinutes(1));

        // When
        var output = await fixture.Handler().HandleAsync(Command(target.PublicId));

        // Then
        Assert.True(output.Success);
    }
}
