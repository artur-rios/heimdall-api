using ArturRios.Heimdall.Command.Handlers;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Tests.Support;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Mock;

namespace ArturRios.Heimdall.Command.Tests;

// Unit tests for DeleteScopeCommandHandler (UC-04).
// Cover the main flow, the cascade to Users/Google Users/applications, AF-04a (not found), and
// AF-04b (already deleted, idempotent, the cascade re-run for members still live). Authorization
// (403/401) is a functional concern, and so are the atomicity and the set-based statements (#204),
// which ScopeDeleteCascadeTests shows against PostgreSQL.
public class DeleteScopeCommandHandlerTests
{
    // One fake per aggregate, passed as its reader; the scope's flag goes through the atomic writes.
    private sealed record Fakes(
        AsyncFakeRepository<Scope, long> Scopes,
        AsyncFakeRepository<Person, long> Persons,
        AsyncFakeRepository<GoogleUser, long> GoogleUsers,
        AsyncFakeRepository<Application, long> Applications)
    {
        public InMemoryWriteTransactions Transactions { get; } = new();
        public InMemoryInvariantLocks Locks { get; } = new();

        public DeleteScopeCommandHandler Handler() => new(
            Scopes, InMemoryAtomicWrites.Instance, Persons, GoogleUsers, Applications,
            new InMemoryScopeCascades(Persons, GoogleUsers, Applications), Transactions, Locks);
    }

    private static async Task<Fakes> EmptyFakes()
    {
        await Task.CompletedTask;
        return new Fakes(
            new AsyncFakeRepository<Scope, long>(),
            new AsyncFakeRepository<Person, long>(),
            new AsyncFakeRepository<GoogleUser, long>(),
            new AsyncFakeRepository<Application, long>());
    }

    private static async Task<Scope> SeedScopeAsync(Fakes fakes, bool isDeleted = false)
    {
        var scope = new Scope { PublicId = Guid.NewGuid(), Name = $"scope-{Guid.NewGuid():N}", IsDeleted = isDeleted };
        await fakes.Scopes.CreateAsync(scope);
        return scope;
    }

    private static async Task SeedUserAsync(Fakes fakes, long scopeId, bool isDeleted = false)
    {
        // ScopeMembership is set at construction (the handler's query only reads ScopeMembership.ScopeId),
        // so it is stored with the person regardless of how the fake handles the reference.
        var person = new Person
        {
            PublicId = Guid.NewGuid(),
            IsDeleted = isDeleted,
            ScopeId = scopeId, ScopeMembership = new ScopeUser { ScopeId = scopeId }
        };
        await fakes.Persons.CreateAsync(person);
    }

    private static async Task SeedGoogleUserAsync(Fakes fakes, long scopeId, bool isDeleted = false)
    {
        await fakes.GoogleUsers.CreateAsync(new GoogleUser { PublicId = Guid.NewGuid(), ScopeId = scopeId, IsDeleted = isDeleted });
    }

    private static async Task SeedApplicationAsync(Fakes fakes, long scopeId, bool isDeleted = false)
    {
        await fakes.Applications.CreateAsync(new Application { PublicId = Guid.NewGuid(), ScopeId = scopeId, IsDeleted = isDeleted });
    }

    [UnitFact]
    public async Task GivenScopeWithNoMembers_WhenHandlingDeleteScope_ThenScopeIsDeletedWithZeroCounts()
    {
        // Given
        var fakes = await EmptyFakes();
        var scope = await SeedScopeAsync(fakes);
        var command = new DeleteScopeCommand { Id = scope.PublicId };

        // When
        var output = await fakes.Handler().HandleAsync(command);

        // Then — output
        Assert.True(output.Success);
        Assert.NotNull(output.Data);
        Assert.Equal(scope.PublicId, output.Data!.Id);
        Assert.Equal(0, output.Data.UserCount);
        Assert.Equal(0, output.Data.GoogleUserCount);
        Assert.Equal(0, output.Data.ApplicationCount);
        Assert.Contains(ScopeMessages.ScopeDeletedSuccessfully, output.Messages);

        // Then — the scope is flipped in the store
        var stored = (await fakes.Scopes.GetAllAsync()).Data!.Single();
        Assert.True(stored.IsDeleted);
    }

    [UnitFact]
    public async Task GivenScopeWithMembers_WhenHandlingDeleteScope_ThenMembersAreLogicallyDeletedAndCounted()
    {
        // Given a scope with two Users, one Google User, and one application
        var fakes = await EmptyFakes();
        var scope = await SeedScopeAsync(fakes);
        await SeedUserAsync(fakes, scope.Id);
        await SeedUserAsync(fakes, scope.Id);
        await SeedGoogleUserAsync(fakes, scope.Id);
        await SeedApplicationAsync(fakes, scope.Id);
        var command = new DeleteScopeCommand { Id = scope.PublicId };

        // When
        var output = await fakes.Handler().HandleAsync(command);

        // Then — counts reflect the totals
        Assert.True(output.Success);
        Assert.Equal(2, output.Data!.UserCount);
        Assert.Equal(1, output.Data.GoogleUserCount);
        Assert.Equal(1, output.Data.ApplicationCount);

        // Then — every member is flipped, the identities as an administrative deletion, in the one
        // transaction that flipped the scope
        Assert.All((await fakes.Persons.GetAllAsync()).Data!, p =>
        {
            Assert.True(p.IsDeleted);
            Assert.Equal((int)DeletionKinds.Administrative, p.DeletionKind);
            Assert.NotNull(p.DeletedAt);
        });
        Assert.All((await fakes.GoogleUsers.GetAllAsync()).Data!, g =>
        {
            Assert.True(g.IsDeleted);
            Assert.Equal((int)DeletionKinds.Administrative, g.DeletionKind);
        });
        Assert.All((await fakes.Applications.GetAllAsync()).Data!, a => Assert.True(a.IsDeleted));
        Assert.Equal(1, fakes.Transactions.Committed);
    }

    [UnitFact]
    public async Task GivenScopeWithAlreadyDeletedMember_WhenHandlingDeleteScope_ThenMemberStillCounted()
    {
        // Given a scope whose single User is already individually logically deleted
        var fakes = await EmptyFakes();
        var scope = await SeedScopeAsync(fakes);
        await SeedUserAsync(fakes, scope.Id, isDeleted: true);
        var command = new DeleteScopeCommand { Id = scope.PublicId };

        // When
        var output = await fakes.Handler().HandleAsync(command);

        // Then — the already-deleted User is still part of the total
        Assert.True(output.Success);
        Assert.Equal(1, output.Data!.UserCount);
    }

    [UnitFact]
    public async Task GivenMissingScope_WhenHandlingDeleteScope_ThenReturnsScopeNotFound()
    {
        // Given an empty store
        var fakes = await EmptyFakes();
        var command = new DeleteScopeCommand { Id = Guid.NewGuid() };

        // When
        var output = await fakes.Handler().HandleAsync(command);

        // Then
        Assert.False(output.Success);
        Assert.Contains(ScopeMessages.ScopeNotFound, output.Errors);
    }

    [UnitFact]
    public async Task GivenAlreadyDeletedScope_WhenHandlingDeleteScope_ThenSucceedsIdempotentlyWithTotals()
    {
        // Given a logically deleted scope that still has one application
        var fakes = await EmptyFakes();
        var scope = await SeedScopeAsync(fakes, isDeleted: true);
        await SeedApplicationAsync(fakes, scope.Id, isDeleted: true);
        var command = new DeleteScopeCommand { Id = scope.PublicId };

        // When
        var output = await fakes.Handler().HandleAsync(command);

        // Then — idempotent success, totals still reported
        Assert.True(output.Success);
        Assert.Equal(scope.PublicId, output.Data!.Id);
        Assert.Equal(1, output.Data.ApplicationCount);
        Assert.Contains(ScopeMessages.ScopeDeletedSuccessfully, output.Messages);
    }

    [UnitFact]
    public async Task GivenAlreadyDeletedScopeWithALiveMember_WhenHandlingDeleteScope_ThenTheCascadeIsFinished()
    {
        // Given a deleted scope whose cascade did not finish: a User and a Google User still live (#204)
        var fakes = await EmptyFakes();
        var scope = await SeedScopeAsync(fakes, isDeleted: true);
        await SeedUserAsync(fakes, scope.Id);
        await SeedGoogleUserAsync(fakes, scope.Id);
        var command = new DeleteScopeCommand { Id = scope.PublicId };

        // When
        var output = await fakes.Handler().HandleAsync(command);

        // Then — AF-04b still answers success, and the members left live are deleted
        Assert.True(output.Success);
        Assert.True(Assert.Single((await fakes.Persons.GetAllAsync()).Data!).IsDeleted);
        Assert.True(Assert.Single((await fakes.GoogleUsers.GetAllAsync()).Data!).IsDeleted);
    }

    // #202: the scope's row is locked inside the transaction, as every owner-removing writer locks it.

    [UnitFact]
    public async Task GivenAScope_WhenHandlingDeleteScopeCommand_ThenItsRowIsLocked()
    {
        // Given
        var fakes = await EmptyFakes();
        var scope = await SeedScopeAsync(fakes);

        // When
        var output = await fakes.Handler().HandleAsync(new DeleteScopeCommand { Id = scope.PublicId });

        // Then
        Assert.True(output.Success);
        Assert.Equal([scope.Id], Assert.Single(fakes.Locks.ScopeLocks));
    }
}
