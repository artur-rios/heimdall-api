using ArturRios.Heimdall.Command.Handlers;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Mock;

namespace ArturRios.Heimdall.Command.Tests;

// Unit tests for RestrictProcessingCommandHandler (UC-44). The property that matters most is that
// restriction and deletion are independent: a restricted record must be preserved exactly as it
// stands, which is the opposite of what the deletion flag sets in motion.
public class RestrictProcessingCommandHandlerTests
{
    private static (AsyncFakeRepository<Person>, AsyncFakeRepository<GoogleUser>) Fakes() => (new(), new());

    private static RestrictProcessingCommandHandler Handler(
        AsyncFakeRepository<Person> persons, AsyncFakeRepository<GoogleUser> googleUsers) =>
        new(persons, persons, googleUsers, googleUsers);

    private static async Task<Person> SeedPersonAsync(
        AsyncFakeRepository<Person> persons, bool isDeleted = false, DateTime? restrictedAt = null)
    {
        var person = new Person
        {
            PublicId = Guid.NewGuid(),
            Name = "Ada",
            Email = $"ada-{Guid.NewGuid():N}@test.local",
            RoleId = (long)Roles.User,
            IsDeleted = isDeleted,
            ProcessingRestrictedAt = restrictedAt
        };

        await persons.CreateAsync(person);

        return person;
    }

    private static RestrictProcessingCommand Command(
        Guid actingPersonId, RestrictionGrounds ground = RestrictionGrounds.AccuracyContested) =>
        new() { ActingPersonId = actingPersonId, ActingRole = (int)Roles.User, Ground = (int)ground };

    [UnitFact]
    public async Task GivenAPerson_WhenRestricting_ThenTheRestrictionIsRecorded()
    {
        var (persons, googleUsers) = Fakes();
        var person = await SeedPersonAsync(persons);

        var output = await Handler(persons, googleUsers).HandleAsync(Command(person.PublicId));

        Assert.True(output.Success);
        Assert.Equal(ErasureMessages.ProcessingRestricted, output.Messages.First());
        Assert.NotNull(person.ProcessingRestrictedAt);
        Assert.Equal((int)RestrictionGrounds.AccuracyContested, person.RestrictionGround);
    }

    [UnitFact]
    public async Task GivenAPerson_WhenRestricting_ThenNothingIsDeleted()
    {
        // The whole point: restriction preserves the record exactly as it stands. Reusing the
        // deletion flag would set NFR-20's erasure clock running on data the subject has
        // specifically asked be kept.
        var (persons, googleUsers) = Fakes();
        var person = await SeedPersonAsync(persons);

        await Handler(persons, googleUsers).HandleAsync(Command(person.PublicId));

        Assert.False(person.IsDeleted);
        Assert.Null(person.DeletedAt);
        Assert.Null(person.AnonymisedAt);
        Assert.Equal("Ada", person.Name);
    }

    [UnitFact]
    public async Task GivenAlreadyRestricted_WhenRestrictingAgain_ThenItIsRefused()
    {
        var (persons, googleUsers) = Fakes();
        var person = await SeedPersonAsync(persons, restrictedAt: DateTime.UtcNow.AddDays(-1));

        var output = await Handler(persons, googleUsers).HandleAsync(Command(person.PublicId));

        Assert.False(output.Success);
        Assert.Contains(ErasureMessages.ProcessingAlreadyRestricted, output.Errors);
    }

    [UnitTheory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(-1)]
    public async Task GivenAGroundOutsideArticle18_WhenRestricting_ThenItIsRefused(int ground)
    {
        // Art. 18(1) is exhaustive: a restriction rests on one of its four grounds or it is not a
        // restriction under the article.
        var (persons, googleUsers) = Fakes();
        var person = await SeedPersonAsync(persons);

        var output = await Handler(persons, googleUsers)
            .HandleAsync(new RestrictProcessingCommand { ActingPersonId = person.PublicId, Ground = ground });

        Assert.False(output.Success);
        Assert.Contains(ErasureMessages.RestrictionGroundUnknown, output.Errors);
        Assert.Null(person.ProcessingRestrictedAt);
    }

    [UnitFact]
    public async Task GivenASuspendedPerson_WhenRestricting_ThenItStillApplies()
    {
        // A suspended identity may still contest the accuracy of what is held about it, and Art. 18
        // does not require the account to be in good standing.
        var (persons, googleUsers) = Fakes();
        var person = await SeedPersonAsync(persons, isDeleted: true);

        var output = await Handler(persons, googleUsers).HandleAsync(Command(person.PublicId));

        Assert.True(output.Success);
        Assert.NotNull(person.ProcessingRestrictedAt);

        // And the deletion state is untouched — the two are independent in both directions
        Assert.True(person.IsDeleted);
    }

    [UnitFact]
    public async Task GivenAGoogleUser_WhenRestricting_ThenItApplies()
    {
        var (persons, googleUsers) = Fakes();
        var googleUser = new GoogleUser
        {
            PublicId = Guid.NewGuid(), GoogleId = "1", Name = "Ada", Email = "ada@test.local"
        };
        await googleUsers.CreateAsync(googleUser);

        var output = await Handler(persons, googleUsers)
            .HandleAsync(Command(googleUser.PublicId, RestrictionGrounds.ObjectionPending));

        Assert.True(output.Success);
        Assert.Equal((int)RestrictionGrounds.ObjectionPending, googleUser.RestrictionGround);
    }

    [UnitFact]
    public async Task GivenATokenNamingNobody_WhenRestricting_ThenItIsRefused()
    {
        var (persons, googleUsers) = Fakes();

        var output = await Handler(persons, googleUsers).HandleAsync(Command(Guid.NewGuid()));

        Assert.False(output.Success);
        Assert.Contains(ErasureMessages.NotEligible, output.Errors);
    }
}
