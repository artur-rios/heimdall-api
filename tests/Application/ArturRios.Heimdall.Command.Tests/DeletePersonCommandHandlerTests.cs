using ArturRios.Heimdall.Command.Handlers;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Tests.Support;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Domain.Persistence;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Heimdall.Shared.Services;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Mock;
using Moq;

namespace ArturRios.Heimdall.Command.Tests;

// Unit tests for DeletePersonCommandHandler (UC-09). Cover the main flow for each permitted actor,
// AF-09a (not found), AF-09b (already deleted, idempotent), AF-09c (not authorized), AF-09d
// (self-deletion), and AF-09e (the NFR-12 last-owner guard).
//
// The role gate that keeps a plain User out of the endpoint is a [RoleRequirement] concern and is
// asserted in PersonControllerDeleteTests, not here (Testing Specification §6.4).
public class DeletePersonCommandHandlerTests
{
    private static Scope Scope(long id) => new() { Id = id, PublicId = Guid.NewGuid(), Name = $"scope-{id}" };

    private static Person User(long id, Scope scope, bool isDeleted = false) => new()
    {
        Id = id,
        PublicId = Guid.NewGuid(),
        Name = $"user-{id}",
        Email = $"user-{id}@test.local",
        RoleId = (long)Roles.User,
        IsDeleted = isDeleted,
        UpdatedAt = Stamp,
        ScopeId = scope.Id, ScopeMembership = new ScopeUser { ScopeId = scope.Id, Scope = scope }
    };

    private static Person ScopeAdmin(long id, bool isDeleted = false, params Scope[] owned) => new()
    {
        Id = id,
        PublicId = Guid.NewGuid(),
        Name = $"admin-{id}",
        Email = $"admin-{id}@test.local",
        RoleId = (long)Roles.ScopeAdmin,
        IsDeleted = isDeleted,
        UpdatedAt = Stamp,
        ScopeOwnerships = owned.Select(scope => new ScopeOwner { ScopeId = scope.Id, Scope = scope }).ToList()
    };

    // A fixed, obviously-not-now timestamp, so "UpdatedAt was not touched" is a meaningful assertion.
    private static readonly DateTime Stamp = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static IScopeOwnershipChecker Ownership(bool allowed)
    {
        var checker = new Mock<IScopeOwnershipChecker>();
        checker
            .Setup(x => x.ActorMayManageScopeAsync(It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<long>()))
            .ReturnsAsync(allowed);

        return checker.Object;
    }

    private static async Task<AsyncFakeRepository<Person, long>> PersonsWith(params Person[] persons)
    {
        var repository = new AsyncFakeRepository<Person, long>();

        foreach (var person in persons)
        {
            await repository.CreateAsync(person);
        }

        return repository;
    }

    private static DeletePersonCommandHandler HandlerFor(
        AsyncFakeRepository<Person, long> persons, bool ownershipAllowed = true, IAtomicWrites? atomicWrites = null,
        InMemoryWriteTransactions? transactions = null, InMemoryInvariantLocks? locks = null) =>
        new(persons, Ownership(ownershipAllowed), atomicWrites ?? InMemoryAtomicWrites.Instance,
            transactions ?? InMemoryWriteTransactions.Instance, locks ?? InMemoryInvariantLocks.Instance);

    private static DeletePersonCommand CommandFor(Person target, int actingRole, Guid actingPersonId) => new()
    {
        Id = target.PublicId,
        ActingRole = actingRole,
        ActingPersonId = actingPersonId
    };

    [UnitFact]
    public async Task GivenSystemAdmin_WhenHandlingDeletePerson_ThenPersonIsFlaggedDeleted()
    {
        // Given
        var scope = Scope(1);
        var target = User(10, scope);
        var persons = await PersonsWith(target);
        var command = CommandFor(target, (int)Roles.SystemAdmin, actingPersonId: Guid.NewGuid());

        // When
        var output = await HandlerFor(persons).HandleAsync(command);

        // Then — output
        Assert.True(output.Success);
        Assert.NotNull(output.Data);
        Assert.Equal(target.PublicId, output.Data!.Id);
        Assert.False(output.Data.AlreadyDeleted);
        Assert.Contains(PersonMessages.PersonDeletedSuccessfully, output.Messages);

        // Then — the person is flagged and stamped
        Assert.True(target.IsDeleted);
        Assert.NotEqual(Stamp, target.UpdatedAt);
    }

    [UnitFact]
    public async Task GivenOwningScopeAdmin_WhenHandlingDeletePerson_ThenUserIsFlaggedDeleted()
    {
        // Given a Scope Admin who owns the target User's scope
        var scope = Scope(1);
        var target = User(10, scope);
        var persons = await PersonsWith(target);
        var command = CommandFor(target, (int)Roles.ScopeAdmin, actingPersonId: Guid.NewGuid());

        // When
        var output = await HandlerFor(persons, ownershipAllowed: true).HandleAsync(command);

        // Then
        Assert.True(output.Success);
        Assert.False(output.Data!.AlreadyDeleted);
        Assert.True(target.IsDeleted);
    }

    [UnitFact]
    public async Task GivenScopeAdminWithCoOwnedScopes_WhenHandlingDeletePerson_ThenPersonIsFlaggedDeleted()
    {
        // Given a ScopeAdmin target whose only owned scope has another owner
        var scope = Scope(1);
        var target = ScopeAdmin(10, owned: scope);
        var coOwner = ScopeAdmin(11, owned: scope);
        var persons = await PersonsWith(target, coOwner);
        var command = CommandFor(target, (int)Roles.SystemAdmin, actingPersonId: Guid.NewGuid());

        // When
        var output = await HandlerFor(persons).HandleAsync(command);

        // Then
        Assert.True(output.Success);
        Assert.True(target.IsDeleted);
        Assert.False(coOwner.IsDeleted);
    }

    [UnitFact]
    public async Task GivenPersonDoesNotExist_WhenHandlingDeletePerson_ThenReturnsPersonNotFoundError()
    {
        // Given — AF-09a
        var persons = await PersonsWith();
        var command = new DeletePersonCommand
        {
            Id = Guid.NewGuid(), ActingRole = (int)Roles.SystemAdmin, ActingPersonId = Guid.NewGuid()
        };

        // When
        var output = await HandlerFor(persons).HandleAsync(command);

        // Then
        Assert.False(output.Success);
        Assert.Contains(PersonMessages.PersonNotFound, output.Errors);
    }

    [UnitFact]
    public async Task GivenPersonAlreadyDeleted_WhenHandlingDeletePerson_ThenReturnsSuccessWithoutWriting()
    {
        // Given — AF-09b
        var scope = Scope(1);
        var target = User(10, scope, isDeleted: true);
        var persons = await PersonsWith(target);
        var command = CommandFor(target, (int)Roles.SystemAdmin, actingPersonId: Guid.NewGuid());

        // When
        var output = await HandlerFor(persons).HandleAsync(command);

        // Then — an idempotent success that wrote nothing
        Assert.True(output.Success);
        Assert.True(output.Data!.AlreadyDeleted);
        Assert.Contains(PersonMessages.PersonDeletedSuccessfully, output.Messages);
        Assert.True(target.IsDeleted);
        Assert.Equal(Stamp, target.UpdatedAt);
    }

    [UnitFact]
    public async Task GivenPersonDeletedSinceTheRead_WhenHandlingDeletePerson_ThenTheFirstDeletionStands()
    {
        // Given a deletion another request makes between this one's read and its write
        var scope = Scope(1);
        var target = User(10, scope);
        var persons = await PersonsWith(target);
        var command = CommandFor(target, (int)Roles.SystemAdmin, actingPersonId: Guid.NewGuid());
        var firstDeletion = new DateTime(2021, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var atomicWrites = new InMemoryAtomicWrites
        {
            BeforeConditionalWrite = row =>
            {
                row.IsDeleted = true;
                row.DeletedAt = firstDeletion;
                row.DeletionKind = (int)DeletionKinds.SubjectRequested;
            }
        };

        // When
        var output = await HandlerFor(persons, atomicWrites: atomicWrites).HandleAsync(command);

        // Then it answers as AF-09b does, and the first deletion's time and kind stand
        Assert.True(output.Success);
        Assert.True(output.Data!.AlreadyDeleted);
        Assert.Equal(firstDeletion, target.DeletedAt);
        Assert.Equal((int)DeletionKinds.SubjectRequested, target.DeletionKind);
    }

    [UnitFact]
    public async Task GivenSoleOwnerAlreadyDeleted_WhenHandlingDeletePerson_ThenReturnsSuccessInsteadOfConflict()
    {
        // Given a ScopeAdmin who is the only owner of a scope and is already deleted: AF-09b must
        // win over the last-owner guard, or a required idempotent success becomes a 409.
        var scope = Scope(1);
        var target = ScopeAdmin(10, isDeleted: true, owned: scope);
        var persons = await PersonsWith(target);
        var command = CommandFor(target, (int)Roles.SystemAdmin, actingPersonId: Guid.NewGuid());

        // When
        var output = await HandlerFor(persons).HandleAsync(command);

        // Then
        Assert.True(output.Success);
        Assert.True(output.Data!.AlreadyDeleted);
        Assert.DoesNotContain(PersonMessages.ScopeWouldLoseLastOwner, output.Errors);
    }

    [UnitFact]
    public async Task GivenScopeAdminNotOwningTargetScope_WhenHandlingDeletePerson_ThenReturnsNotAuthorizedError()
    {
        // Given — AF-09c: the ownership check refuses
        var scope = Scope(1);
        var target = User(10, scope);
        var persons = await PersonsWith(target);
        var command = CommandFor(target, (int)Roles.ScopeAdmin, actingPersonId: Guid.NewGuid());

        // When
        var output = await HandlerFor(persons, ownershipAllowed: false).HandleAsync(command);

        // Then
        Assert.False(output.Success);
        Assert.Contains(PersonMessages.NotAuthorizedToDeletePerson, output.Errors);
        Assert.False(target.IsDeleted);
    }

    [UnitFact]
    public async Task GivenScopeAdminTargetingAnotherScopeAdmin_WhenHandlingDeletePerson_ThenReturnsNotAuthorizedError()
    {
        // Given — AF-09c: a Scope Admin may only delete Users, never another admin
        var scope = Scope(1);
        var target = ScopeAdmin(10, owned: scope);
        var persons = await PersonsWith(target);
        var command = CommandFor(target, (int)Roles.ScopeAdmin, actingPersonId: Guid.NewGuid());

        // When — ownership would allow it; the role of the target is what refuses
        var output = await HandlerFor(persons, ownershipAllowed: true).HandleAsync(command);

        // Then
        Assert.False(output.Success);
        Assert.Contains(PersonMessages.NotAuthorizedToDeletePerson, output.Errors);
        Assert.False(target.IsDeleted);
    }

    [UnitFact]
    public async Task GivenActorTargetingThemselves_WhenHandlingDeletePerson_ThenReturnsCannotDeleteSelfError()
    {
        // Given — AF-09d, even for a System Admin
        var target = ScopeAdmin(10);
        target.RoleId = (long)Roles.SystemAdmin;
        var persons = await PersonsWith(target);
        var command = CommandFor(target, (int)Roles.SystemAdmin, actingPersonId: target.PublicId);

        // When
        var output = await HandlerFor(persons).HandleAsync(command);

        // Then
        Assert.False(output.Success);
        Assert.Contains(PersonMessages.CannotDeleteSelf, output.Errors);
        Assert.False(target.IsDeleted);
    }

    [UnitFact]
    public async Task GivenSoleOwnerScopeAdmin_WhenHandlingDeletePerson_ThenReturnsScopeWouldLoseLastOwnerError()
    {
        // Given — AF-09e: nobody else owns the scope
        var scope = Scope(1);
        var target = ScopeAdmin(10, owned: scope);
        var unrelated = ScopeAdmin(11, owned: Scope(2));
        var persons = await PersonsWith(target, unrelated);
        var command = CommandFor(target, (int)Roles.SystemAdmin, actingPersonId: Guid.NewGuid());

        // When
        var output = await HandlerFor(persons).HandleAsync(command);

        // Then
        Assert.False(output.Success);
        Assert.Contains(PersonMessages.ScopeWouldLoseLastOwner, output.Errors);
        Assert.False(target.IsDeleted);
        Assert.Equal(Stamp, target.UpdatedAt);
    }

    [UnitFact]
    public async Task GivenSoleOwnerSystemAdmin_WhenHandlingDeletePerson_ThenReturnsScopeWouldLoseLastOwnerError()
    {
        // Given — NFR-12 holds whatever role the sole owner has
        var target = ScopeAdmin(10, owned: Scope(1));
        target.RoleId = (long)Roles.SystemAdmin;
        var persons = await PersonsWith(target);
        var command = CommandFor(target, (int)Roles.SystemAdmin, actingPersonId: Guid.NewGuid());

        // When
        var output = await HandlerFor(persons).HandleAsync(command);

        // Then
        Assert.False(output.Success);
        Assert.Contains(PersonMessages.ScopeWouldLoseLastOwner, output.Errors);
        Assert.False(target.IsDeleted);
    }

    [UnitTheory]
    [InlineData(InactiveOwner.Restricted)]
    [InlineData(InactiveOwner.ErasureRequested)]
    public async Task GivenOnlyCoOwnerIsInactive_WhenHandlingDeletePerson_ThenReturnsScopeWouldLoseLastOwnerError(
        InactiveOwner how)
    {
        // Given — AF-09e: the scope's other owner is restricted or waiting on their erasure, and neither
        // keeps the scope owned (#211)
        var scope = Scope(1);
        var target = ScopeAdmin(10, owned: scope);
        var coOwner = InactiveOwners.Make(ScopeAdmin(11, owned: scope), how);
        var persons = await PersonsWith(target, coOwner);
        var command = CommandFor(target, (int)Roles.SystemAdmin, actingPersonId: Guid.NewGuid());

        // When
        var output = await HandlerFor(persons).HandleAsync(command);

        // Then
        Assert.Equal([PersonMessages.ScopeWouldLoseLastOwner], output.Errors);
        Assert.False(target.IsDeleted);
    }

    // #202: the guard and the deletion run under the locks of the person's scopes, in one transaction.

    [UnitFact]
    public async Task GivenACoOwner_WhenHandlingDeletePerson_ThenTheirScopesAreLockedAndTheDeletionCommitted()
    {
        // Given
        var first = Scope(1);
        var second = Scope(2);
        var target = ScopeAdmin(10, owned: [first, second]);
        var coOwner = ScopeAdmin(11, owned: [first, second]);
        var persons = await PersonsWith(target, coOwner);
        var transactions = new InMemoryWriteTransactions();
        var locks = new InMemoryInvariantLocks();
        var command = CommandFor(target, (int)Roles.SystemAdmin, actingPersonId: Guid.NewGuid());

        // When
        var output = await HandlerFor(persons, transactions: transactions, locks: locks).HandleAsync(command);

        // Then
        Assert.True(output.Success);
        Assert.Equivalent(new[] { 1L, 2L }, Assert.Single(locks.ScopeLocks), strict: true);
        Assert.Equal(1, transactions.Committed);
    }

    [UnitFact]
    public async Task GivenTheSoleOwner_WhenHandlingDeletePerson_ThenRefusedUnderTheLockAndNothingCommitted()
    {
        // Given
        var target = ScopeAdmin(10, owned: Scope(1));
        var persons = await PersonsWith(target);
        var transactions = new InMemoryWriteTransactions();
        var locks = new InMemoryInvariantLocks();
        var command = CommandFor(target, (int)Roles.SystemAdmin, actingPersonId: Guid.NewGuid());

        // When
        var output = await HandlerFor(persons, transactions: transactions, locks: locks).HandleAsync(command);

        // Then
        Assert.Equal([PersonMessages.ScopeWouldLoseLastOwner], output.Errors);
        Assert.Equal([1L], Assert.Single(locks.ScopeLocks));
        Assert.Equal(0, transactions.Committed);
    }

    // #203 (NFR-32): no deletion may leave the system without an active System Admin.

    private static Person SystemAdmin(long id) => new()
    {
        Id = id,
        PublicId = Guid.NewGuid(),
        Name = $"system-admin-{id}",
        Email = $"system-admin-{id}@test.local",
        RoleId = (long)Roles.SystemAdmin,
        UpdatedAt = Stamp
    };

    [UnitTheory]
    [InlineData(InactiveOwner.Restricted)]
    [InlineData(InactiveOwner.ErasureRequested)]
    public async Task GivenTheOnlyOtherSystemAdminIsInactive_WhenDeletingTheActiveOne_ThenReturnsWouldRemoveLastSystemAdmin(
        InactiveOwner how)
    {
        // Given — AF-09f: the actor is the other administrator, restricted or waiting on their erasure
        var target = SystemAdmin(10);
        var actor = InactiveOwners.Make(SystemAdmin(11), how);
        var persons = await PersonsWith(target, actor);
        var locks = new InMemoryInvariantLocks();
        var command = CommandFor(target, (int)Roles.SystemAdmin, actingPersonId: actor.PublicId);

        // When
        var output = await HandlerFor(persons, locks: locks).HandleAsync(command);

        // Then — refused under the System Admin lock, and the administrator stays
        Assert.Equal([PersonMessages.WouldRemoveLastSystemAdmin], output.Errors);
        Assert.False(target.IsDeleted);
        Assert.Equal(1, locks.SystemAdminLocks);
    }

    [UnitFact]
    public async Task GivenAnotherActiveSystemAdmin_WhenDeletingASystemAdmin_ThenDeletedUnderTheSystemAdminLock()
    {
        // Given
        var target = SystemAdmin(10);
        var actor = SystemAdmin(11);
        var persons = await PersonsWith(target, actor);
        var locks = new InMemoryInvariantLocks();
        var command = CommandFor(target, (int)Roles.SystemAdmin, actingPersonId: actor.PublicId);

        // When
        var output = await HandlerFor(persons, locks: locks).HandleAsync(command);

        // Then
        Assert.True(output.Success);
        Assert.True(target.IsDeleted);
        Assert.Equal(1, locks.SystemAdminLocks);
    }

    [UnitFact]
    public async Task GivenTheOnlySystemAdmin_WhenDeletingThemselves_ThenReturnsCannotDeleteSelf()
    {
        // Given the last System Admin (AF-09d answers before anything is locked)
        var target = SystemAdmin(10);
        var persons = await PersonsWith(target);
        var command = CommandFor(target, (int)Roles.SystemAdmin, actingPersonId: target.PublicId);

        // When
        var output = await HandlerFor(persons).HandleAsync(command);

        // Then
        Assert.Equal([PersonMessages.CannotDeleteSelf], output.Errors);
        Assert.False(target.IsDeleted);
    }

    [UnitFact]
    public async Task GivenAScopeAdmin_WhenHandlingDeletePerson_ThenTheSystemAdminSetIsNotLocked()
    {
        // Given — only a System Admin's deletion can touch NFR-32, so no other waits on the set
        var target = ScopeAdmin(10, owned: Scope(1));
        var coOwner = ScopeAdmin(11, owned: Scope(1));
        var persons = await PersonsWith(target, coOwner);
        var locks = new InMemoryInvariantLocks();
        var command = CommandFor(target, (int)Roles.SystemAdmin, actingPersonId: Guid.NewGuid());

        // When
        var output = await HandlerFor(persons, locks: locks).HandleAsync(command);

        // Then
        Assert.True(output.Success);
        Assert.Equal(0, locks.SystemAdminLocks);
    }
}
