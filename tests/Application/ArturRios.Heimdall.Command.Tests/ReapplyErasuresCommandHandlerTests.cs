using ArturRios.Heimdall.Command.Handlers;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Services;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Mock;

namespace ArturRios.Heimdall.Command.Tests;

// Unit tests for ReapplyErasuresCommandHandler (NFR-25). This is the step that keeps a restore from
// silently undoing an erasure, so what it does with a ledger entry that matches nothing, and with
// one it has already handled, matters as much as the happy path — a restore is run under pressure
// and the step may be repeated.
public class ReapplyErasuresCommandHandlerTests
{
    private static (AsyncFakeRepository<Person>, AsyncFakeRepository<GoogleUser>) Fakes() => (new(), new());

    private static ReapplyErasuresCommandHandler Handler(
        AsyncFakeRepository<Person> persons, AsyncFakeRepository<GoogleUser> googleUsers) =>
        new(persons, persons, googleUsers, googleUsers);

    private static async Task<Person> SeedPersonAsync(
        AsyncFakeRepository<Person> persons, DateTime? anonymisedAt = null)
    {
        var person = new Person
        {
            PublicId = Guid.NewGuid(),
            Name = "Ada Lovelace",
            Email = $"ada-{Guid.NewGuid():N}@test.local",
            PasswordHash = [1, 2, 3],
            Salt = [4, 5, 6],
            RoleId = (long)Roles.User,
            AnonymisedAt = anonymisedAt
        };

        await persons.CreateAsync(person);

        return person;
    }

    private static ReapplyErasuresCommand Command(params Guid[] ids) =>
        new() { SubjectIds = ids, ActingRole = (int)Roles.SystemAdmin };

    [UnitFact]
    public async Task GivenARestoredIdentity_WhenReapplying_ThenItIsAnonymisedAgain()
    {
        var (persons, googleUsers) = Fakes();
        var person = await SeedPersonAsync(persons);

        var output = await Handler(persons, googleUsers).HandleAsync(Command(person.PublicId));

        Assert.True(output.Success);
        Assert.Equal(ErasureMessages.ErasuresReapplied, output.Messages.First());
        Assert.Equal(1, output.Data!.Anonymised);
        Assert.Equal(IdentityAnonymiser.AnonymisedName, person.Name);
        Assert.NotNull(person.AnonymisedAt);

        // The record's own state says why it is gone, so the trail reads correctly afterwards
        Assert.True(person.IsDeleted);
        Assert.Equal((int)DeletionKinds.SubjectRequested, person.DeletionKind);
    }

    [UnitFact]
    public async Task GivenAnIdentityAlreadyAnonymised_WhenReapplying_ThenItIsCountedNotRewritten()
    {
        // The restore did not in fact bring this one back. Rewriting it would move AnonymisedAt and
        // make the record claim the erasure happened later than it did.
        var (persons, googleUsers) = Fakes();
        var alreadyDone = DateTime.UtcNow.AddDays(-30);
        var person = await SeedPersonAsync(persons, anonymisedAt: alreadyDone);

        var output = await Handler(persons, googleUsers).HandleAsync(Command(person.PublicId));

        Assert.Equal(0, output.Data!.Anonymised);
        Assert.Equal(1, output.Data.AlreadyAnonymised);
        Assert.Equal(alreadyDone, person.AnonymisedAt);
    }

    [UnitFact]
    public async Task GivenALedgerEntryMatchingNothing_WhenReapplying_ThenItIsReportedNotFailed()
    {
        // A ledger covers every erasure ever completed, while a restore reinstates only what one
        // backup held — so most of the list matching nothing is the expected case. Treating it as
        // failure would make the step look broken every time it worked.
        var (persons, googleUsers) = Fakes();
        var missing = Guid.NewGuid();

        var output = await Handler(persons, googleUsers).HandleAsync(Command(missing));

        Assert.True(output.Success);
        Assert.Equal(0, output.Data!.Anonymised);
        Assert.Equal(missing, Assert.Single(output.Data.NotFound));
    }

    [UnitFact]
    public async Task GivenAnEmptyLedger_WhenReapplying_ThenItIsRefused()
    {
        // Running the reconciliation with nothing to reconcile almost always means the ledger was
        // not loaded. Reporting success would let a restore be signed off with erased people back in
        // the database.
        var (persons, googleUsers) = Fakes();

        var output = await Handler(persons, googleUsers).HandleAsync(Command());

        Assert.False(output.Success);
        Assert.Contains(ErasureMessages.NoSubjectsToReapply, output.Errors);
    }

    [UnitFact]
    public async Task GivenTheSameLedgerTwice_WhenReapplying_ThenTheSecondRunChangesNothing()
    {
        // A restore is run under pressure and the runbook step may be repeated or half-completed
        var (persons, googleUsers) = Fakes();
        var person = await SeedPersonAsync(persons);
        var handler = Handler(persons, googleUsers);

        await handler.HandleAsync(Command(person.PublicId));
        var anonymisedAt = person.AnonymisedAt;

        var second = await handler.HandleAsync(Command(person.PublicId));

        Assert.Equal(0, second.Data!.Anonymised);
        Assert.Equal(1, second.Data.AlreadyAnonymised);
        Assert.Equal(anonymisedAt, person.AnonymisedAt);
    }

    [UnitFact]
    public async Task GivenAMixedLedger_WhenReapplying_ThenEachOutcomeIsCountedSeparately()
    {
        var (persons, googleUsers) = Fakes();
        var restored = await SeedPersonAsync(persons);
        var alreadyDone = await SeedPersonAsync(persons, anonymisedAt: DateTime.UtcNow.AddDays(-1));
        var missing = Guid.NewGuid();

        var googleUser = new GoogleUser
        {
            PublicId = Guid.NewGuid(), GoogleId = "1", Name = "Ada", Email = "ada@test.local"
        };
        await googleUsers.CreateAsync(googleUser);

        var output = await Handler(persons, googleUsers)
            .HandleAsync(Command(restored.PublicId, alreadyDone.PublicId, googleUser.PublicId, missing));

        Assert.Equal(2, output.Data!.Anonymised);
        Assert.Equal(1, output.Data.AlreadyAnonymised);
        Assert.Equal(missing, Assert.Single(output.Data.NotFound));
        Assert.NotEqual("1", googleUser.GoogleId);
    }
}
