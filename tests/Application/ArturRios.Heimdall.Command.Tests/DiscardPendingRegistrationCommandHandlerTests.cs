using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Data.Relational.Core.Repositories;
using ArturRios.Heimdall.Command.Handlers;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Heimdall.Shared.Services;
using ArturRios.Output;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Mock;
using Moq;

namespace ArturRios.Heimdall.Command.Tests;

// Unit tests for DiscardPendingRegistrationCommandHandler (UC-50, FR-SR-12): the row is removed and
// its public id returned as the audit target; a closed scope's row can still be discarded; AF-50a
// (unknown or deleted scope), AF-50b (not an owner), AF-50c (unknown, or belonging to another scope);
// and a delete that loses a race to a confirmation or the purge answers "not found", not a conflict.
public class DiscardPendingRegistrationCommandHandlerTests
{
    // A store whose delete fails as EF reports a row deleted by a concurrent request first.
    private sealed class ConcurrentlyDeletedPendingRepository
        : AsyncFakeRepository<PendingRegistration, long>, IAsyncRepository<PendingRegistration, long>
    {
        Task<DataOutput<IEnumerable<long>>> IAsyncRepository<PendingRegistration, long>.DeleteRangeAsync(
            IEnumerable<long> ids, CancellationToken cancellationToken) =>
            Task.FromResult(DataOutput<IEnumerable<long>>.New.WithError(RelationalErrors.ConcurrencyMessage));
    }

    // A store whose delete fails for a reason that is not a lost race.
    private sealed class FailingDeletePendingRepository
        : AsyncFakeRepository<PendingRegistration, long>, IAsyncRepository<PendingRegistration, long>
    {
        Task<DataOutput<IEnumerable<long>>> IAsyncRepository<PendingRegistration, long>.DeleteRangeAsync(
            IEnumerable<long> ids, CancellationToken cancellationToken) =>
            Task.FromResult(DataOutput<IEnumerable<long>>.New.WithError(RelationalErrors.TransientMessage));
    }

    private static Scope Scope(long id, bool isDeleted = false, bool open = true) => new()
    {
        Id = id,
        PublicId = Guid.NewGuid(),
        Name = $"scope-{id}",
        IsDeleted = isDeleted,
        SelfRegistrationEnabled = open
    };

    private static PendingRegistration Pending(long id, Scope scope) => new()
    {
        Id = id,
        PublicId = Guid.NewGuid(),
        ScopeId = scope.Id,
        Scope = scope,
        Name = $"Registrant {id}",
        Email = $"registrant-{id}@test.local",
        PasswordHash = [1],
        Salt = [2],
        TokenHash = $"hash-{id}",
        CreatedAt = DateTime.UtcNow,
        ExpiresAt = DateTime.UtcNow.AddHours(1)
    };

    private static IScopeOwnershipChecker Ownership(bool allowed)
    {
        var checker = new Mock<IScopeOwnershipChecker>();
        checker
            .Setup(x => x.ActorMayManageScopeAsync(It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<long>()))
            .ReturnsAsync(allowed);

        return checker.Object;
    }

    private static async Task<AsyncFakeRepository<Scope, long>> ScopesWith(params Scope[] scopes)
    {
        var repository = new AsyncFakeRepository<Scope, long>();

        foreach (var scope in scopes)
        {
            await repository.CreateAsync(scope);
        }

        return repository;
    }

    private static async Task<TRepository> Seed<TRepository>(TRepository repository, params PendingRegistration[] rows)
        where TRepository : AsyncFakeRepository<PendingRegistration, long>
    {
        foreach (var row in rows)
        {
            await repository.CreateAsync(row);
        }

        return repository;
    }

    private static DiscardPendingRegistrationCommandHandler Handler(
        AsyncFakeRepository<Scope, long> scopes,
        AsyncFakeRepository<PendingRegistration, long> pending,
        bool ownershipAllowed = true) =>
        new(scopes, pending, pending, Ownership(ownershipAllowed));

    private static DiscardPendingRegistrationCommand Command(Scope scope, Guid registrationId) => new()
    {
        ScopeId = scope.PublicId,
        RegistrationId = registrationId,
        ActingPersonId = Guid.NewGuid(),
        ActingRole = (int)Roles.ScopeAdmin
    };

    [UnitFact]
    public async Task GivenARegistrationOfTheScope_WhenDiscarding_ThenItIsRemovedAndItsIdReturned()
    {
        // Given
        var scope = Scope(1);
        var row = Pending(1, scope);
        var pending = await Seed(new AsyncFakeRepository<PendingRegistration, long>(), row);
        var handler = Handler(await ScopesWith(scope), pending);

        // When
        var output = await handler.HandleAsync(Command(scope, row.PublicId));

        // Then — the id is what the audit entry names as target
        Assert.True(output.Success);
        Assert.Contains(ScopeMessages.PendingRegistrationDiscarded, output.Messages);
        Assert.Equal(row.PublicId, output.Data!.Id);
        Assert.Empty(pending.Query());
    }

    [UnitFact]
    public async Task GivenAScopeClosedToSelfRegistration_WhenDiscarding_ThenTheRowIsRemoved()
    {
        // Given sign-up closed after the registration was stored
        var scope = Scope(1, open: false);
        var row = Pending(1, scope);
        var pending = await Seed(new AsyncFakeRepository<PendingRegistration, long>(), row);
        var handler = Handler(await ScopesWith(scope), pending);

        // When
        var output = await handler.HandleAsync(Command(scope, row.PublicId));

        // Then
        Assert.True(output.Success);
        Assert.Empty(pending.Query());
    }

    [UnitFact]
    public async Task GivenAnUnknownScope_WhenDiscarding_ThenReturnsScopeNotFound()
    {
        // Given AF-50a
        var scope = Scope(1);
        var row = Pending(1, scope);
        var pending = await Seed(new AsyncFakeRepository<PendingRegistration, long>(), row);
        var handler = Handler(await ScopesWith(scope), pending);

        // When
        var output = await handler.HandleAsync(Command(Scope(99), row.PublicId));

        // Then
        Assert.False(output.Success);
        Assert.Contains(ScopeMessages.ScopeNotFound, output.Errors);
        Assert.Single(pending.Query());
    }

    [UnitFact]
    public async Task GivenALogicallyDeletedScope_WhenDiscarding_ThenReturnsScopeNotFound()
    {
        // Given AF-50a
        var scope = Scope(1, isDeleted: true);
        var row = Pending(1, scope);
        var pending = await Seed(new AsyncFakeRepository<PendingRegistration, long>(), row);
        var handler = Handler(await ScopesWith(scope), pending);

        // When
        var output = await handler.HandleAsync(Command(scope, row.PublicId));

        // Then
        Assert.Contains(ScopeMessages.ScopeNotFound, output.Errors);
        Assert.Single(pending.Query());
    }

    [UnitFact]
    public async Task GivenACallerWhoDoesNotOwnTheScope_WhenDiscarding_ThenReturnsNotScopeOwnerAndKeepsTheRow()
    {
        // Given AF-50b
        var scope = Scope(1);
        var row = Pending(1, scope);
        var pending = await Seed(new AsyncFakeRepository<PendingRegistration, long>(), row);
        var handler = Handler(await ScopesWith(scope), pending, ownershipAllowed: false);

        // When
        var output = await handler.HandleAsync(Command(scope, row.PublicId));

        // Then
        Assert.False(output.Success);
        Assert.Contains(ScopeMessages.NotScopeOwner, output.Errors);
        Assert.Single(pending.Query());
    }

    [UnitFact]
    public async Task GivenARegistrationOfAnotherScope_WhenDiscarding_ThenReturnsNotFoundAndKeepsTheRow()
    {
        // Given AF-50c — the caller owns scope 1; the registration belongs to scope 2. It must look
        // exactly like a missing one.
        var scope = Scope(1);
        var other = Scope(2);
        var row = Pending(1, other);
        var pending = await Seed(new AsyncFakeRepository<PendingRegistration, long>(), row);
        var handler = Handler(await ScopesWith(scope, other), pending);

        // When
        var output = await handler.HandleAsync(Command(scope, row.PublicId));

        // Then
        Assert.False(output.Success);
        Assert.Contains(ScopeMessages.PendingRegistrationNotFound, output.Errors);
        Assert.Single(pending.Query());
    }

    [UnitFact]
    public async Task GivenAnUnknownRegistration_WhenDiscarding_ThenReturnsPendingRegistrationNotFound()
    {
        // Given AF-50c
        var scope = Scope(1);
        var pending = await Seed(new AsyncFakeRepository<PendingRegistration, long>(), Pending(1, scope));
        var handler = Handler(await ScopesWith(scope), pending);

        // When
        var output = await handler.HandleAsync(Command(scope, Guid.NewGuid()));

        // Then
        Assert.Contains(ScopeMessages.PendingRegistrationNotFound, output.Errors);
    }

    [UnitFact]
    public async Task GivenTheDeleteLosesARace_WhenDiscarding_ThenReturnsPendingRegistrationNotFound()
    {
        // Given the row was confirmed or purged between the lookup and the delete
        var scope = Scope(1);
        var row = Pending(1, scope);
        var pending = await Seed(new ConcurrentlyDeletedPendingRepository(), row);
        var handler = Handler(await ScopesWith(scope), pending);

        // When
        var output = await handler.HandleAsync(Command(scope, row.PublicId));

        // Then — the row is gone either way; "not found" is the truth, a conflict would not be
        Assert.False(output.Success);
        Assert.Contains(ScopeMessages.PendingRegistrationNotFound, output.Errors);
        Assert.DoesNotContain(RelationalErrors.ConcurrencyMessage, output.Errors);
    }

    [UnitFact]
    public async Task GivenTheDeleteFailsOtherwise_WhenDiscarding_ThenTheFailureIsReturned()
    {
        // Given the database refuses the delete for another reason
        var scope = Scope(1);
        var row = Pending(1, scope);
        var pending = await Seed(new FailingDeletePendingRepository(), row);
        var handler = Handler(await ScopesWith(scope), pending);

        // When
        var output = await handler.HandleAsync(Command(scope, row.PublicId));

        // Then — classified by DataAccessMessageMap (503), not disguised as not-found
        Assert.False(output.Success);
        Assert.Contains(RelationalErrors.TransientMessage, output.Errors);
    }
}
