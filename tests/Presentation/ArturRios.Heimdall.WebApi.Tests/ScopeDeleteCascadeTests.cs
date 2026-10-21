using ArturRios.Data.Relational.Core.Repositories;
using ArturRios.Heimdall.Command.Handlers;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Data.Configuration;
using ArturRios.Heimdall.Data.Persistence;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.WebApi.Tests.Support;
using ArturRios.Output;
using ArturRios.Util.Test.Attributes;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Heimdall.WebApi.Tests;

/// <summary>
///     A scope's delete cascade against the real database (#204): all or nothing, finished by a retry when
///     an earlier attempt left members live, and set-based, so a large tenant costs a few statements and
///     no member is loaded.
/// </summary>
/// <remarks>
///     The handlers run over EF repositories on a context carrying a <see cref="FailingCommand" /> or a
///     <see cref="CommandLog" />. The 5,000-member tests pin the set-based path by what the context sent:
///     one <c>UPDATE</c> per kind of member, deletes in batches of 2,000, and no read of a member's
///     password hash.
/// </remarks>
[Collection(nameof(FunctionalCollection))]
public class ScopeDeleteCascadeTests(PostgresFixture db)
{
    private const int LargeTenant = 5000;

    [FunctionalFact]
    public async Task GivenAFailureMidCascade_WhenDeletingAScopeAndRetrying_ThenNoMemberIsLeftLive()
    {
        // Given a scope with Users, a Google User and an application
        var scope = await SeedScopeAsync();
        var users = await SeedUsersAsync(scope, 2);
        await SeedGoogleUserAsync(scope);
        await SeedApplicationAsync(scope, users[0]);
        var fault = new FailingCommand("UPDATE", "google_user");

        // When the cascade fails at the Google Users
        await using (var context = db.CreateContext(fault))
        {
            Assert.False(await SucceedsAsync(() => DeleteScopeHandler(context)
                .HandleAsync(new DeleteScopeCommand { Id = scope.PublicId })));
        }

        // Then nothing was kept, the scope's flag included
        Assert.True(fault.Fired);
        Assert.False((await ScopeAsync(scope.Id)).IsDeleted);
        Assert.Equal(2, await LiveUsersAsync(scope.Id));

        // And when it is retried, every member is deleted
        await using (var retry = db.CreateContext())
        {
            var output = await DeleteScopeHandler(retry).HandleAsync(new DeleteScopeCommand { Id = scope.PublicId });

            Assert.True(output.Success, string.Join(" | ", output.Errors));
        }

        Assert.True((await ScopeAsync(scope.Id)).IsDeleted);
        await AssertNoLiveMemberAsync(scope.Id);
    }

    [FunctionalFact]
    public async Task GivenADeletedScopeWithLiveMembers_WhenDeletingItAgain_ThenTheCascadeIsFinished()
    {
        // Given a scope already flagged deleted whose cascade never ran — what an attempt before #204 left
        // when it failed part-way — and one User deleted on their own request long before
        var scope = await SeedScopeAsync(isDeleted: true);
        var users = await SeedUsersAsync(scope, 2);
        await SeedGoogleUserAsync(scope);
        await SeedApplicationAsync(scope, users[0]);
        var deletedAt = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var alreadyDeleted = (await SeedUsersAsync(scope, 1, deletedAt))[0];

        // When (AF-04b)
        await using (var context = db.CreateContext())
        {
            var output = await DeleteScopeHandler(context).HandleAsync(new DeleteScopeCommand { Id = scope.PublicId });

            Assert.True(output.Success, string.Join(" | ", output.Errors));
            Assert.Equal(3, output.Data!.UserCount);
        }

        // Then every member is deleted, administratively, and the earlier deletion keeps its own time and kind
        await AssertNoLiveMemberAsync(scope.Id);

        await using var check = db.CreateContext();
        var user = await check.Persons.SingleAsync(x => x.Id == users[1].Id);
        Assert.Equal((int)DeletionKinds.Administrative, user.DeletionKind);
        Assert.NotNull(user.DeletedAt);

        var earlier = await check.Persons.SingleAsync(x => x.Id == alreadyDeleted.Id);
        Assert.Equal(deletedAt, earlier.DeletedAt);
        Assert.Equal((int)DeletionKinds.SubjectRequested, earlier.DeletionKind);
    }

    [FunctionalFact]
    public async Task GivenAScopeOf5000Users_WhenDeletingIt_ThenEveryUserIsDeletedByOneStatement()
    {
        // Given
        var scope = await SeedScopeAsync();
        await SeedUsersAsync(scope, LargeTenant);
        var log = new CommandLog();

        // When
        await using (var context = db.CreateContext(log))
        {
            var output = await DeleteScopeHandler(context).HandleAsync(new DeleteScopeCommand { Id = scope.PublicId });

            Assert.True(output.Success, string.Join(" | ", output.Errors));
            Assert.Equal(LargeTenant, output.Data!.UserCount);
        }

        // Then every User is deleted, by one statement, and none was loaded
        Assert.Equal(0, await LiveUsersAsync(scope.Id));
        Assert.Equal(1, log.CountStartingWith("UPDATE heimdall.person"));
        Assert.DoesNotContain(log.Commands, command => command.Contains("password_hash"));
    }

    [FunctionalFact]
    public async Task GivenAScopeOf5000Users_WhenHardDeletingIt_ThenEveryUserIsRemovedInBatchesAndTombstoned()
    {
        // Given
        var scope = await SeedScopeAsync();
        var users = await SeedUsersAsync(scope, LargeTenant);
        var log = new CommandLog();

        // When
        await using (var context = db.CreateContext(log))
        {
            var output = await HardDeleteScopeHandler(context)
                .HandleAsync(new HardDeleteScopeCommand { Id = scope.PublicId });

            Assert.True(output.Success, string.Join(" | ", output.Errors));
            Assert.Equal(LargeTenant, output.Data!.UserCount);
        }

        // Then every User is gone and tombstoned, removed 2,000 at a time, and none was loaded
        await using var check = db.CreateContext();
        var ids = users.Select(user => user.Id).ToList();
        var publicIds = users.Select(user => user.PublicId).ToList();
        Assert.False(await check.Persons.AnyAsync(x => ids.Contains(x.Id)));
        Assert.Equal(LargeTenant, await check.ErasedSubjects.CountAsync(x => publicIds.Contains(x.Id)));
        Assert.Equal(3, log.CountStartingWith("DELETE FROM heimdall.person"));
        Assert.DoesNotContain(log.Commands, command => command.Contains("password_hash"));
    }

    private static DeleteScopeCommandHandler DeleteScopeHandler(AppDbContext context) => new(
        new EfRepository<Scope, long>(context),
        new EfRepository<Scope, long>(context),
        new EfRepository<Person, long>(context),
        new EfRepository<GoogleUser, long>(context),
        new EfRepository<Application, long>(context),
        new ScopeCascades(context),
        new WriteTransactions(context));

    private static HardDeleteScopeCommandHandler HardDeleteScopeHandler(AppDbContext context) => new(
        new EfRepository<Scope, long>(context),
        new EfRepository<Scope, long>(context),
        new EfRepository<Person, long>(context),
        new EfRepository<GoogleUser, long>(context),
        new EfRepository<Application, long>(context),
        new EfRepository<ScopePermission, long>(context),
        new ErasedSubjects(context),
        new ScopeCascades(context),
        new WriteTransactions(context));

    /// <summary>Whether a command succeeded, counting one that threw — as a failed write may — as failed.</summary>
    private static async Task<bool> SucceedsAsync<T>(Func<Task<DataOutput<T>>> run)
    {
        try
        {
            return (await run()).Success;
        }
        catch (InjectedDatabaseFault)
        {
            return false;
        }
    }

    private async Task AssertNoLiveMemberAsync(long scopeId)
    {
        await using var check = db.CreateContext();
        Assert.Equal(0, await LiveUsersAsync(scopeId));
        Assert.False(await check.GoogleUsers.AnyAsync(x => x.ScopeId == scopeId && !x.IsDeleted));
        Assert.False(await check.Applications.AnyAsync(x => x.ScopeId == scopeId && !x.IsDeleted));
    }

    private async Task<int> LiveUsersAsync(long scopeId)
    {
        await using var context = db.CreateContext();
        return await context.Persons.CountAsync(x =>
            !x.IsDeleted && context.ScopeUsers.Any(member => member.PersonId == x.Id && member.ScopeId == scopeId));
    }

    private async Task<Scope> ScopeAsync(long id)
    {
        await using var context = db.CreateContext();
        return await context.Scopes.SingleAsync(x => x.Id == id);
    }

    private async Task<Scope> SeedScopeAsync(bool isDeleted = false)
    {
        await using var context = db.CreateContext();
        var scope = new Scope { PublicId = Guid.NewGuid(), Name = $"scope-{Guid.NewGuid():N}", IsDeleted = isDeleted };
        context.Scopes.Add(scope);
        await context.SaveChangesAsync();
        return scope;
    }

    /// <summary>
    ///     Seeds <paramref name="count" /> Users of the scope with their <c>SCOPE_USER</c> rows, in two saves
    ///     however many there are; logically deleted on their own request at <paramref name="deletedAt" />
    ///     when it is given.
    /// </summary>
    private async Task<IReadOnlyList<Person>> SeedUsersAsync(Scope scope, int count, DateTime? deletedAt = null)
    {
        await using var context = db.CreateContext();
        var users = Enumerable.Range(0, count)
            .Select(_ => new Person
            {
                PublicId = Guid.NewGuid(),
                Name = "Member",
                Email = $"member-{Guid.NewGuid():N}@test.local",
                RoleId = (long)Roles.User,
                EmailVerified = true,
                ScopeId = scope.Id,
                IsDeleted = deletedAt is not null,
                DeletedAt = deletedAt,
                DeletionKind = deletedAt is null ? null : (int)DeletionKinds.SubjectRequested
            })
            .ToList();
        context.Persons.AddRange(users);
        await context.SaveChangesAsync();

        context.ScopeUsers.AddRange(users.Select(user => new ScopeUser { ScopeId = scope.Id, PersonId = user.Id }));
        await context.SaveChangesAsync();

        return users;
    }

    private async Task SeedGoogleUserAsync(Scope scope)
    {
        await using var context = db.CreateContext();
        context.GoogleUsers.Add(new GoogleUser
        {
            PublicId = Guid.NewGuid(),
            GoogleId = Guid.NewGuid().ToString("N"),
            Name = "Google User",
            Email = $"google-{Guid.NewGuid():N}@test.local",
            ScopeId = scope.Id
        });
        await context.SaveChangesAsync();
    }

    private async Task SeedApplicationAsync(Scope scope, Person owner)
    {
        await using var context = db.CreateContext();
        context.Applications.Add(new Application
        {
            PublicId = Guid.NewGuid(), Name = $"app-{Guid.NewGuid():N}", ScopeId = scope.Id, OwnerId = owner.Id
        });
        await context.SaveChangesAsync();
    }
}
