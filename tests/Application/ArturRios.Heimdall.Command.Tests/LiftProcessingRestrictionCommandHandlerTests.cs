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

// Unit tests for LiftProcessingRestrictionCommandHandler (UC-45). The behaviour worth pinning is
// Art. 18(3): the subject is informed *before* the restriction is lifted, which makes a failed
// notification a refusal rather than something to log and carry on from — the opposite of how every
// other delivery in this API behaves.
public class LiftProcessingRestrictionCommandHandlerTests
{
    private static IRestrictionLiftNotifier Notifier(bool succeeds, Mock<IRestrictionLiftNotifier>? capture = null)
    {
        var notifier = capture ?? new Mock<IRestrictionLiftNotifier>();
        notifier.Setup(n => n.NotifyAsync(It.IsAny<string>())).ReturnsAsync(succeeds);
        return notifier.Object;
    }

    private static LiftProcessingRestrictionCommandHandler Handler(
        AsyncFakeRepository<Person> persons,
        AsyncFakeRepository<GoogleUser> googleUsers,
        IRestrictionLiftNotifier notifier) =>
        new(persons, persons, googleUsers, googleUsers, notifier);

    private static async Task<Person> SeedRestrictedAsync(AsyncFakeRepository<Person> persons)
    {
        var person = new Person
        {
            PublicId = Guid.NewGuid(),
            Name = "Ada",
            Email = $"ada-{Guid.NewGuid():N}@test.local",
            RoleId = (long)Roles.User,
            ProcessingRestrictedAt = DateTime.UtcNow.AddDays(-3),
            RestrictionGround = (int)RestrictionGrounds.AccuracyContested
        };

        await persons.CreateAsync(person);

        return person;
    }

    [UnitFact]
    public async Task GivenASystemAdmin_WhenLifting_ThenTheSubjectIsInformedFirst()
    {
        var persons = new AsyncFakeRepository<Person>();
        var person = await SeedRestrictedAsync(persons);
        var notifier = new Mock<IRestrictionLiftNotifier>();

        var output = await Handler(persons, new(), Notifier(true, notifier))
            .HandleAsync(new LiftProcessingRestrictionCommand
            {
                SubjectId = person.PublicId,
                ActingPersonId = Guid.NewGuid(),
                ActingRole = (int)Roles.SystemAdmin
            });

        Assert.True(output.Success);
        Assert.True(output.Data!.SubjectNotified);
        notifier.Verify(n => n.NotifyAsync(person.Email), Times.Once);

        Assert.Null(person.ProcessingRestrictedAt);
        Assert.Null(person.RestrictionGround);
        Assert.NotNull(person.RestrictionLiftNotifiedAt);
    }

    [UnitFact]
    public async Task GivenTheNotificationFails_WhenLifting_ThenTheRestrictionStands()
    {
        // Art. 18(3) makes informing a precondition of the act. A lift that proceeded after a failed
        // send would be unlawful and would look identical to one that worked.
        var persons = new AsyncFakeRepository<Person>();
        var person = await SeedRestrictedAsync(persons);

        var output = await Handler(persons, new(), Notifier(succeeds: false))
            .HandleAsync(new LiftProcessingRestrictionCommand
            {
                SubjectId = person.PublicId,
                ActingPersonId = Guid.NewGuid(),
                ActingRole = (int)Roles.SystemAdmin
            });

        Assert.False(output.Success);
        Assert.Contains(ErasureMessages.RestrictionLiftNotificationFailed, output.Errors);
        Assert.NotNull(person.ProcessingRestrictedAt);
    }

    [UnitFact]
    public async Task GivenTheSubjectLiftsTheirOwn_WhenLifting_ThenNoNotificationIsSent()
    {
        // They are the person Art. 18(3) exists to inform. Requiring an email to somebody standing
        // in front of you, and refusing their request when it bounces, would be the article's letter
        // against its purpose.
        var persons = new AsyncFakeRepository<Person>();
        var person = await SeedRestrictedAsync(persons);
        var notifier = new Mock<IRestrictionLiftNotifier>();

        var output = await Handler(persons, new(), Notifier(true, notifier))
            .HandleAsync(new LiftProcessingRestrictionCommand
            {
                ActingPersonId = person.PublicId,
                ActingRole = (int)Roles.User
            });

        Assert.True(output.Success);
        Assert.False(output.Data!.SubjectNotified);
        notifier.Verify(n => n.NotifyAsync(It.IsAny<string>()), Times.Never);
        Assert.Null(person.ProcessingRestrictedAt);
    }

    [UnitFact]
    public async Task GivenANonAdminLiftingSomebodyElses_WhenLifting_ThenItIsRefused()
    {
        var persons = new AsyncFakeRepository<Person>();
        var person = await SeedRestrictedAsync(persons);

        var output = await Handler(persons, new(), Notifier(true))
            .HandleAsync(new LiftProcessingRestrictionCommand
            {
                SubjectId = person.PublicId,
                ActingPersonId = Guid.NewGuid(),
                ActingRole = (int)Roles.ScopeAdmin
            });

        Assert.False(output.Success);
        Assert.Contains(ErasureMessages.NotEligible, output.Errors);
        Assert.NotNull(person.ProcessingRestrictedAt);
    }

    [UnitFact]
    public async Task GivenNoRestriction_WhenLifting_ThenItIsReportedAsNotRestricted()
    {
        var persons = new AsyncFakeRepository<Person>();
        var person = new Person
        {
            PublicId = Guid.NewGuid(), Name = "Ada", Email = "ada@test.local", RoleId = (long)Roles.User
        };
        await persons.CreateAsync(person);

        var output = await Handler(persons, new(), Notifier(true))
            .HandleAsync(new LiftProcessingRestrictionCommand
            {
                ActingPersonId = person.PublicId, ActingRole = (int)Roles.User
            });

        Assert.False(output.Success);
        Assert.Contains(ErasureMessages.NotRestricted, output.Errors);
    }

    [UnitFact]
    public async Task GivenARestrictedGoogleUser_WhenAnAdminLifts_ThenTheyAreInformedToo()
    {
        var googleUsers = new AsyncFakeRepository<GoogleUser>();
        var googleUser = new GoogleUser
        {
            PublicId = Guid.NewGuid(),
            GoogleId = "1",
            Name = "Ada",
            Email = "ada@test.local",
            ProcessingRestrictedAt = DateTime.UtcNow.AddDays(-1),
            RestrictionGround = (int)RestrictionGrounds.ObjectionPending
        };
        await googleUsers.CreateAsync(googleUser);

        var notifier = new Mock<IRestrictionLiftNotifier>();

        var output = await Handler(new(), googleUsers, Notifier(true, notifier))
            .HandleAsync(new LiftProcessingRestrictionCommand
            {
                SubjectId = googleUser.PublicId,
                ActingPersonId = Guid.NewGuid(),
                ActingRole = (int)Roles.SystemAdmin
            });

        Assert.True(output.Success);
        notifier.Verify(n => n.NotifyAsync(googleUser.Email), Times.Once);
        Assert.Null(googleUser.ProcessingRestrictedAt);
    }
}
