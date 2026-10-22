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

namespace ArturRios.Heimdall.WebApi.Tests;

/// <summary>
///     The system's only two System Admins deleting each other at the same moment, against the real database
///     and as the runtime login (#203): exactly one deletion may succeed, and one active System Admin remains.
/// </summary>
/// <remarks>
///     <para>
///         NFR-32 counts every active System Admin in the database, so the test runs in a database of its own
///         (<see cref="IsolatedDatabase" />), where the seeded master user is the first administrator and the
///         test adds the second.
///     </para>
///     <para>
///         The overlap is made as in <see cref="ScopeOwnerRaceTests" />: the first deletion is held just before
///         its write, its guard already answered, and the second is let run until PostgreSQL reports it waiting
///         on a lock — or, without the System Admin lock, until it has finished. Without the lock each sees the
///         other still active, and both administrators end up deleted.
///     </para>
/// </remarks>
[Collection(nameof(FunctionalCollection))]
public class SystemAdminRaceTests(PostgresFixture db)
{
    [FunctionalFact]
    public async Task GivenTheOnlyTwoSystemAdmins_WhenEachDeletesTheOtherAtOnce_ThenOneIsRefusedAndOneRemains()
    {
        // Given a database whose only System Admins are the master user and one more
        await using var isolated = await IsolatedDatabase.CreateAsync(db);
        var master = (await SystemAdminsAsync(isolated)).Single();
        var second = await Seeds.PersonAsync(isolated, Roles.SystemAdmin, new PersonOptions { EmailPrefix = "second-admin" });
        var hold = new PauseBefore("UPDATE heimdall.person");
        var log = new CommandLog();
        await using var held = Runtime(isolated, hold, log);
        await using var other = Runtime(isolated);
        var otherPid = await LockWaits.OpenAndGetPidAsync(other);

        // When the master deletes the second, and the second the master while the first deletion is about
        // to write
        var firstDeletion = DeletePerson(held).HandleAsync(Deletion(second, by: master));
        await hold.Reached.WaitAsync(TimeSpan.FromSeconds(30));
        var secondDeletion = DeletePerson(other).HandleAsync(Deletion(master, by: second));
        await LockWaits.WaitUntilBlockedOrDoneAsync(db, otherPid, secondDeletion);
        hold.Resume();
        var outputs = await Task.WhenAll(firstDeletion, secondDeletion);

        // Then the first went through, the second met AF-09f, and the master is still an active System Admin
        Assert.True(outputs[0].Success);
        Assert.Equal([PersonMessages.WouldRemoveLastSystemAdmin], outputs[1].Errors);
        Assert.Equal([master.Id], (await SystemAdminsAsync(isolated)).Select(admin => admin.Id));
        Assert.Contains(log.Commands, command => command.StartsWith("SELECT pg_advisory_xact_lock("));
    }

    private static AppDbContext Runtime(IsolatedDatabase isolated, params IInterceptor[] interceptors) =>
        PostgresFixture.CreateContext(isolated.RuntimeConnectionString, interceptors);

    private static DeletePersonCommand Deletion(Person target, Person by) => new()
    {
        Id = target.PublicId, ActingRole = (int)Roles.SystemAdmin, ActingPersonId = by.PublicId
    };

    private static DeletePersonCommandHandler DeletePerson(AppDbContext context)
    {
        var persons = new EfRepository<Person, long>(context);

        return new DeletePersonCommandHandler(persons, new ScopeOwnershipChecker(persons), new AtomicWrites(context),
            new WriteTransactions(context), new InvariantLocks(context));
    }

    /// <summary>The database's active System Admins (NFR-32).</summary>
    private static async Task<List<Person>> SystemAdminsAsync(IsolatedDatabase isolated)
    {
        await using var context = isolated.CreateContext();

        return await context.Persons.AsNoTracking()
            .Where(Person.Active)
            .Where(person => person.RoleId == (long)Roles.SystemAdmin)
            .ToListAsync();
    }
}
