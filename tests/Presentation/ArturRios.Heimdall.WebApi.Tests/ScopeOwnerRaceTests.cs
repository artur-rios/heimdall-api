using ArturRios.Data.Relational.Core.Repositories;
using ArturRios.Heimdall.Command.Handlers;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Data.Configuration;
using ArturRios.Heimdall.Data.Persistence;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Heimdall.Shared.Services;
using ArturRios.Heimdall.WebApi.Tests.Support;
using ArturRios.Util.Test.Attributes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using static ArturRios.Heimdall.WebApi.Tests.Support.ApplicationSeeds;

namespace ArturRios.Heimdall.WebApi.Tests;

/// <summary>
///     Two requests taking out the two owners of one scope at the same moment, against the real database and
///     as the runtime login (#202): exactly one may succeed, and the scope must keep an active owner.
/// </summary>
/// <remarks>
///     <para>
///         The overlap is made, not hoped for. The first request runs until just before its write and is held
///         there (<see cref="PauseBefore" />), its guard already answered. The second is then started, and the
///         test waits until PostgreSQL reports it blocked on a lock — or, without the lock, finished — before
///         letting the first go. Without the scope lock the second reads the first owner still there, writes,
///         and the first then writes too: both succeed and the scope is left with no active owner.
///     </para>
///     <para>
///         Each request has its own context and connection, as two API requests do, and connects as the
///         runtime login, so the tests also prove that login may take the lock.
///     </para>
/// </remarks>
[Collection(nameof(FunctionalCollection))]
public class ScopeOwnerRaceTests(PostgresFixture db)
{
    private const string ScopeLock = "SELECT 1 FROM heimdall.scope WHERE id = ANY(";

    [FunctionalFact]
    public async Task GivenTwoOwners_WhenBothAreRemovedAtOnce_ThenOneIsRefusedAndOneOwnerRemains()
    {
        // Given a scope with two owners
        var scope = await SeedScopeAsync(db);
        var first = await SeedScopeAdminAsync(db, scope);
        var second = await SeedScopeAdminAsync(db, scope);
        var hold = new PauseBefore("DELETE FROM heimdall.scope_owner");
        var log = new CommandLog();
        await using var held = Runtime(hold, log);
        await using var other = Runtime();
        var otherPid = await LockWaits.OpenAndGetPidAsync(other);

        // When each owner is removed by a request of its own, the second while the first is about to write
        var firstRemoval = RemoveOwner(held).HandleAsync(Removal(scope, first));
        await hold.Reached.WaitAsync(TimeSpan.FromSeconds(30));
        var secondRemoval = RemoveOwner(other).HandleAsync(Removal(scope, second));
        await LockWaits.WaitUntilBlockedOrDoneAsync(db, otherPid, secondRemoval);
        hold.Resume();
        var outputs = await Task.WhenAll(firstRemoval, secondRemoval);

        // Then one removal went through, the other met AF-22b, and one owner is left
        Assert.Single(outputs, output => output.Success);
        Assert.Single(outputs, output => output.Errors.SequenceEqual([PersonMessages.ScopeWouldLoseLastOwner]));
        Assert.Equal(1, await ActiveOwnerCountAsync(scope));
        Assert.Contains(log.Commands, command => command.StartsWith(ScopeLock) && command.EndsWith("FOR UPDATE"));
    }

    [FunctionalFact]
    public async Task GivenTwoOwners_WhenOneIsRemovedWhileTheOtherIsDeleted_ThenOneIsRefusedAndAnActiveOwnerRemains()
    {
        // Given a scope with two owners
        var scope = await SeedScopeAsync(db);
        var first = await SeedScopeAdminAsync(db, scope);
        var second = await SeedScopeAdminAsync(db, scope);
        var hold = new PauseBefore("UPDATE heimdall.person");
        await using var held = Runtime(hold);
        await using var other = Runtime();
        var otherPid = await LockWaits.OpenAndGetPidAsync(other);

        // When the second owner is deleted (UC-09), and the first removed (UC-22) while the deletion is
        // about to write
        var deletion = DeletePerson(held).HandleAsync(new DeletePersonCommand
        {
            Id = second.PublicId, ActingRole = (int)Roles.SystemAdmin, ActingPersonId = Guid.NewGuid()
        });
        await hold.Reached.WaitAsync(TimeSpan.FromSeconds(30));
        var removal = RemoveOwner(other).HandleAsync(Removal(scope, first));
        await LockWaits.WaitUntilBlockedOrDoneAsync(db, otherPid, removal);
        hold.Resume();
        await Task.WhenAll(deletion, removal);

        // Then the deletion, which held the lock, went through; the removal then saw no other active owner
        Assert.True((await deletion).Success);
        Assert.Equal([PersonMessages.ScopeWouldLoseLastOwner], (await removal).Errors);
        Assert.Equal(1, await ActiveOwnerCountAsync(scope));
    }

    private AppDbContext Runtime(params IInterceptor[] interceptors) =>
        PostgresFixture.CreateContext(db.RuntimeConnectionString, interceptors);

    private static RemoveScopeOwnerCommand Removal(Scope scope, Person owner) => new()
    {
        ScopeId = scope.PublicId, PersonId = owner.PublicId,
        ActingRole = (int)Roles.SystemAdmin, ActingPersonId = Guid.NewGuid()
    };

    private static RemoveScopeOwnerCommandHandler RemoveOwner(AppDbContext context)
    {
        var persons = new EfRepository<Person, long>(context);

        return new RemoveScopeOwnerCommandHandler(new EfRepository<Scope, long>(context), persons,
            new ScopeOwnershipChecker(persons), new AtomicWrites(context), new WriteTransactions(context),
            new InvariantLocks(context));
    }

    private static DeletePersonCommandHandler DeletePerson(AppDbContext context)
    {
        var persons = new EfRepository<Person, long>(context);

        return new DeletePersonCommandHandler(persons, new ScopeOwnershipChecker(persons), new AtomicWrites(context),
            new WriteTransactions(context), new InvariantLocks(context));
    }

    /// <summary>The scope's owners who are live, unrestricted and not awaiting erasure (NFR-12).</summary>
    private async Task<int> ActiveOwnerCountAsync(Scope scope)
    {
        await using var context = db.CreateContext();

        return await context.Persons
            .Where(Person.Active)
            .CountAsync(person => person.ScopeOwnerships.Any(ownership => ownership.ScopeId == scope.Id));
    }
}
