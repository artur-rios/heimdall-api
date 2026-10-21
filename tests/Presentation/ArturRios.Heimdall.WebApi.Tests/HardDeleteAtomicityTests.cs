using ArturRios.Data.Relational.Core.Repositories;
using ArturRios.Heimdall.Command.Auditing;
using ArturRios.Heimdall.Command.Handlers;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Data.Configuration;
using ArturRios.Heimdall.Data.Persistence;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Shared.Security;
using ArturRios.Heimdall.WebApi.Tests.Support;
using ArturRios.Mediator.Command;
using ArturRios.Mediator.Command.Interfaces;
using ArturRios.Util.Test.Attributes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArturRios.Heimdall.WebApi.Tests;

/// <summary>
///     A hard delete whose last step fails, against the real database (#212): the tombstones and the
///     dependents written before it must not be kept, because a hard delete is all or nothing.
/// </summary>
/// <remarks>
///     Each handler runs over EF repositories on a context whose <see cref="FailingCommand" /> fails the
///     delete of the root row, and inside <see cref="AuditingCommandHandler{TCommand,TOutput}" /> on the same
///     context, as the API runs it. So the tests also show what the audit entry's save does after the
///     rollback: it records the failure, and writes nothing the rolled-back delete had left pending.
/// </remarks>
[Collection(nameof(FunctionalCollection))]
public class HardDeleteAtomicityTests(PostgresFixture db)
{
    [FunctionalFact]
    public async Task GivenThePersonDeleteFails_WhenHardDeletingAPerson_ThenNothingIsRemovedAndTheFailureIsAudited()
    {
        // Given a person who owns an application and holds both kinds of token
        var scope = await SeedScopeAsync();
        var person = await SeedPersonAsync(Roles.SystemAdmin);
        var application = await SeedApplicationAsync(scope, person);

        await using (var seed = db.CreateContext())
        {
            seed.PasswordResetTokens.Add(new PasswordResetToken
            {
                PersonId = person.Id, TokenHash = $"reset-{Guid.NewGuid():N}", ExpiresAt = DateTime.UtcNow.AddHours(1)
            });
            seed.EmailVerificationTokens.Add(new EmailVerificationToken
            {
                PersonId = person.Id, TokenHash = $"verify-{Guid.NewGuid():N}", ExpiresAt = DateTime.UtcNow.AddHours(1)
            });
            await seed.SaveChangesAsync();
        }

        var fault = new FailingCommand("DELETE", "person");
        var since = DateTime.UtcNow;

        // When the delete of the person fails
        await using (var context = db.CreateContext(fault))
        {
            var output = await Audited(context, HardDeletePersonHandler(context))
                .HandleAsync(new HardDeletePersonCommand { Id = person.PublicId, ActingPersonId = Guid.NewGuid() });

            Assert.False(output.Success);
        }

        // Then the person, their application and tokens are all still there, no tombstone names them, and
        // the attempt is in the trail as a failure
        Assert.True(fault.Fired);

        await using var check = db.CreateContext();
        Assert.True(await check.Persons.AnyAsync(x => x.Id == person.Id));
        Assert.True(await check.Applications.AnyAsync(x => x.Id == application.Id));
        Assert.Equal(1, await check.PasswordResetTokens.CountAsync(x => x.PersonId == person.Id));
        Assert.Equal(1, await check.EmailVerificationTokens.CountAsync(x => x.PersonId == person.Id));
        Assert.False(await check.ErasedSubjects.AnyAsync(x => x.Id == person.PublicId));
        Assert.Equal(1, await FailedEntriesSinceAsync(check, nameof(HardDeletePersonCommand), since));
    }

    [FunctionalFact]
    public async Task GivenTheScopeDeleteFails_WhenHardDeletingAScope_ThenNoMemberIsRemovedAndNoneIsTombstoned()
    {
        // Given a scope with a User, a Google User and an application
        var scope = await SeedScopeAsync();
        var user = await SeedScopeUserAsync(scope);
        var googleUser = await SeedGoogleUserAsync(scope);
        var application = await SeedApplicationAsync(scope, user);
        var fault = new FailingCommand("DELETE", "scope");
        var since = DateTime.UtcNow;

        // When the delete of the scope itself fails
        await using (var context = db.CreateContext(fault))
        {
            var output = await Audited(context, HardDeleteScopeHandler(context))
                .HandleAsync(new HardDeleteScopeCommand { Id = scope.PublicId });

            Assert.False(output.Success);
        }

        // Then every member is still there and none is tombstoned
        Assert.True(fault.Fired);

        await using var check = db.CreateContext();
        Assert.True(await check.Scopes.AnyAsync(x => x.Id == scope.Id));
        Assert.True(await check.Persons.AnyAsync(x => x.Id == user.Id));
        Assert.True(await check.GoogleUsers.AnyAsync(x => x.Id == googleUser.Id));
        Assert.True(await check.Applications.AnyAsync(x => x.Id == application.Id));
        Assert.False(await check.ErasedSubjects.AnyAsync(x => x.Id == user.PublicId || x.Id == googleUser.PublicId));
        Assert.Equal(1, await FailedEntriesSinceAsync(check, nameof(HardDeleteScopeCommand), since));
    }

    private static HardDeletePersonCommandHandler HardDeletePersonHandler(AppDbContext context) => new(
        new EfRepository<Person, long>(context),
        new EfRepository<Person, long>(context),
        new EfRepository<Application, long>(context),
        new EfRepository<Application, long>(context),
        new EfRepository<PasswordResetToken, long>(context),
        new EfRepository<PasswordResetToken, long>(context),
        new EfRepository<EmailVerificationToken, long>(context),
        new EfRepository<EmailVerificationToken, long>(context),
        new ErasedSubjects(context),
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

    /// <summary>The handler as the API runs it: inside the audit decorator, on the same context.</summary>
    private static AuditingCommandHandler<TCommand, TOutput> Audited<TCommand, TOutput>(
        AppDbContext context, ICommandHandlerAsync<TCommand, TOutput> handler)
        where TCommand : BaseCommand
        where TOutput : CommandOutput =>
        new(handler, new AuditLogWriter(new EfRepository<AuditLog, long>(context), new SystemAdminActor()),
            NullLogger<AuditingCommandHandler<TCommand, TOutput>>.Instance);

    private static async Task<int> FailedEntriesSinceAsync(AppDbContext context, string action, DateTime since) =>
        await context.AuditLogs.CountAsync(x => x.Action == action && !x.Succeeded && x.CreatedAt >= since);

    private async Task<Scope> SeedScopeAsync()
    {
        await using var context = db.CreateContext();
        var scope = new Scope { PublicId = Guid.NewGuid(), Name = $"scope-{Guid.NewGuid():N}" };
        context.Scopes.Add(scope);
        await context.SaveChangesAsync();
        return scope;
    }

    private async Task<Person> SeedPersonAsync(Roles role, long? scopeId = null)
    {
        await using var context = db.CreateContext();
        var person = new Person
        {
            PublicId = Guid.NewGuid(),
            Name = "Person",
            Email = $"person-{Guid.NewGuid():N}@test.local",
            RoleId = (long)role,
            EmailVerified = true,
            ScopeId = scopeId
        };
        context.Persons.Add(person);
        await context.SaveChangesAsync();
        return person;
    }

    private async Task<Person> SeedScopeUserAsync(Scope scope)
    {
        var user = await SeedPersonAsync(Roles.User, scope.Id);
        await using var context = db.CreateContext();
        context.ScopeUsers.Add(new ScopeUser { ScopeId = scope.Id, PersonId = user.Id });
        await context.SaveChangesAsync();
        return user;
    }

    private async Task<GoogleUser> SeedGoogleUserAsync(Scope scope)
    {
        await using var context = db.CreateContext();
        var googleUser = new GoogleUser
        {
            PublicId = Guid.NewGuid(),
            GoogleId = Guid.NewGuid().ToString("N"),
            Name = "Google User",
            Email = $"google-{Guid.NewGuid():N}@test.local",
            ScopeId = scope.Id
        };
        context.GoogleUsers.Add(googleUser);
        await context.SaveChangesAsync();
        return googleUser;
    }

    private async Task<Application> SeedApplicationAsync(Scope scope, Person owner)
    {
        await using var context = db.CreateContext();
        var application = new Application
        {
            PublicId = Guid.NewGuid(), Name = $"app-{Guid.NewGuid():N}", ScopeId = scope.Id, OwnerId = owner.Id
        };
        context.Applications.Add(application);
        await context.SaveChangesAsync();
        return application;
    }

    private sealed class SystemAdminActor : IActorAccessor
    {
        public Guid? ActorPersonId => PostgresFixture.StandInPersonIds[Roles.SystemAdmin];
        public int? ActorRole => (int)Roles.SystemAdmin;
    }
}
