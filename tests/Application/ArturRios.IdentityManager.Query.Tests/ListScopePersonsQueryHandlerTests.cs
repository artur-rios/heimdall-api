using ArturRios.IdentityManager.Domain.Entities;
using ArturRios.IdentityManager.Domain.Enums;
using ArturRios.IdentityManager.Query.Handlers;
using ArturRios.IdentityManager.Query.Input;
using ArturRios.IdentityManager.Shared.Messages;
using ArturRios.IdentityManager.Shared.Services;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Mock;
using Moq;

namespace ArturRios.IdentityManager.Query.Tests;

// Unit tests for ListScopePersonsQueryHandler (UC-07, FR-PE-04/FR-PE-08): the scope's Users only,
// paginated and filterable, gated by scope ownership. Covers the main flow, a missing or logically
// deleted scope (AF-07a), a non-owning actor (AF-07b), and the include-deleted behavior.
public class ListScopePersonsQueryHandlerTests
{
    private static Scope Scope(long id, bool isDeleted = false) =>
        new() { Id = id, PublicId = Guid.NewGuid(), Name = $"scope-{id}", IsDeleted = isDeleted };

    private static Person User(long id, Scope scope, string name, string email, bool isDeleted = false) => new()
    {
        Id = id,
        PublicId = Guid.NewGuid(),
        Name = name,
        Email = email,
        RoleId = (long)Roles.User,
        IsDeleted = isDeleted,
        ScopeMembership = new ScopeUser { ScopeId = scope.Id, Scope = scope }
    };

    private static Person Owner(long id, Scope scope) => new()
    {
        Id = id,
        PublicId = Guid.NewGuid(),
        Name = $"owner-{id}",
        Email = $"owner-{id}@test.local",
        RoleId = (long)Roles.ScopeAdmin,
        ScopeOwnerships = [new ScopeOwner { ScopeId = scope.Id, Scope = scope }]
    };

    private static IScopeOwnershipChecker Ownership(bool allowed)
    {
        var checker = new Mock<IScopeOwnershipChecker>();
        checker
            .Setup(x => x.ActorMayManageScopeAsync(It.IsAny<int>(), It.IsAny<long>(), It.IsAny<long>()))
            .ReturnsAsync(allowed);

        return checker.Object;
    }

    private static async Task<AsyncFakeRepository<Scope>> ScopesWith(params Scope[] scopes)
    {
        var repository = new AsyncFakeRepository<Scope>();

        foreach (var scope in scopes)
        {
            await repository.CreateAsync(scope);
        }

        return repository;
    }

    private static async Task<AsyncFakeRepository<Person>> PersonsWith(params Person[] persons)
    {
        var repository = new AsyncFakeRepository<Person>();

        foreach (var person in persons)
        {
            await repository.CreateAsync(person);
        }

        return repository;
    }

    private static ListScopePersonsQuery QueryFor(Scope scope) => new()
    {
        ScopeId = scope.PublicId,
        PageNumber = 1,
        PageSize = 10,
        ActingPersonId = 1,
        ActingRole = (int)Roles.SystemAdmin
    };

    [UnitFact]
    public async Task GivenScopeWithUsers_WhenHandlingListScopePersons_ThenOnlyItsUsersAreReturned()
    {
        // Given a scope with two Users, an owner, and a User of another scope
        var scope = Scope(1);
        var otherScope = Scope(2);
        var member = User(10, scope, "Ana", "ana@test.local");
        var otherMember = User(11, scope, "Bruno", "bruno@test.local");
        var owner = Owner(12, scope);
        var outsider = User(13, otherScope, "Carla", "carla@test.local");
        var scopes = await ScopesWith(scope, otherScope);
        var persons = await PersonsWith(member, otherMember, owner, outsider);
        var handler = new ListScopePersonsQueryHandler(scopes, persons, Ownership(allowed: true));

        // When
        var output = await handler.HandleAsync(QueryFor(scope));

        // Then
        Assert.True(output.Success);
        Assert.Equal(2, output.TotalItems);
        Assert.Equal([member.PublicId, otherMember.PublicId], output.Data!.Select(x => x.Id));
        Assert.Contains(PersonMessages.PersonsRetrievedSuccessfully, output.Messages);
    }

    [UnitFact]
    public async Task GivenUnknownScope_WhenHandlingListScopePersons_ThenReturnsScopeNotFound()
    {
        // Given an empty scope store (AF-07a)
        var scopes = await ScopesWith();
        var persons = await PersonsWith();
        var handler = new ListScopePersonsQueryHandler(scopes, persons, Ownership(allowed: true));

        // When
        var output = await handler.HandleAsync(new ListScopePersonsQuery
        {
            ScopeId = Guid.NewGuid(), PageNumber = 1, PageSize = 10,
            ActingPersonId = 1, ActingRole = (int)Roles.SystemAdmin
        });

        // Then
        Assert.False(output.Success);
        Assert.Contains(PersonMessages.ScopeNotFound, output.Errors);
    }

    [UnitFact]
    public async Task GivenDeletedScope_WhenHandlingListScopePersons_ThenReturnsScopeNotFound()
    {
        // Given a logically deleted scope (AF-07a)
        var scope = Scope(1, isDeleted: true);
        var scopes = await ScopesWith(scope);
        var persons = await PersonsWith();
        var handler = new ListScopePersonsQueryHandler(scopes, persons, Ownership(allowed: true));

        // When
        var output = await handler.HandleAsync(QueryFor(scope));

        // Then
        Assert.False(output.Success);
        Assert.Contains(PersonMessages.ScopeNotFound, output.Errors);
    }

    [UnitFact]
    public async Task GivenActorNotOwningScope_WhenHandlingListScopePersons_ThenReturnsNotScopeOwner()
    {
        // Given an actor the ownership checker rejects (AF-07b)
        var scope = Scope(1);
        var scopes = await ScopesWith(scope);
        var persons = await PersonsWith(User(10, scope, "Ana", "ana@test.local"));
        var handler = new ListScopePersonsQueryHandler(scopes, persons, Ownership(allowed: false));

        // When
        var output = await handler.HandleAsync(QueryFor(scope));

        // Then
        Assert.False(output.Success);
        Assert.Contains(PersonMessages.NotScopeOwner, output.Errors);
    }

    [UnitFact]
    public async Task GivenDeletedUserAndIncludeDeletedFalse_WhenHandlingListScopePersons_ThenUserIsExcluded()
    {
        // Given one active and one logically deleted User (FR-PE-08)
        var scope = Scope(1);
        var active = User(10, scope, "Ana", "ana@test.local");
        var deleted = User(11, scope, "Bruno", "bruno@test.local", isDeleted: true);
        var scopes = await ScopesWith(scope);
        var persons = await PersonsWith(active, deleted);
        var handler = new ListScopePersonsQueryHandler(scopes, persons, Ownership(allowed: true));

        // When
        var output = await handler.HandleAsync(QueryFor(scope));

        // Then
        Assert.Equal(1, output.TotalItems);
        Assert.Equal(active.PublicId, Assert.Single(output.Data!).Id);
    }

    [UnitFact]
    public async Task GivenDeletedUserAndIncludeDeletedTrue_WhenHandlingListScopePersons_ThenUserIsIncluded()
    {
        // Given one active and one logically deleted User (FR-PE-08)
        var scope = Scope(1);
        var active = User(10, scope, "Ana", "ana@test.local");
        var deleted = User(11, scope, "Bruno", "bruno@test.local", isDeleted: true);
        var scopes = await ScopesWith(scope);
        var persons = await PersonsWith(active, deleted);
        var handler = new ListScopePersonsQueryHandler(scopes, persons, Ownership(allowed: true));
        var query = QueryFor(scope);
        query.IncludeDeleted = true;

        // When
        var output = await handler.HandleAsync(query);

        // Then
        Assert.Equal(2, output.TotalItems);
    }

    [UnitFact]
    public async Task GivenNameFilter_WhenHandlingListScopePersons_ThenOnlyMatchingUsersAreReturned()
    {
        // Given two Users with different names; the filter is case-insensitive
        var scope = Scope(1);
        var ana = User(10, scope, "Ana", "ana@test.local");
        var bruno = User(11, scope, "Bruno", "bruno@test.local");
        var scopes = await ScopesWith(scope);
        var persons = await PersonsWith(ana, bruno);
        var handler = new ListScopePersonsQueryHandler(scopes, persons, Ownership(allowed: true));
        var query = QueryFor(scope);
        query.Name = "an";

        // When
        var output = await handler.HandleAsync(query);

        // Then
        Assert.Equal(1, output.TotalItems);
        Assert.Equal(ana.PublicId, Assert.Single(output.Data!).Id);
    }

    [UnitFact]
    public async Task GivenEmailFilter_WhenHandlingListScopePersons_ThenOnlyMatchingUsersAreReturned()
    {
        // Given two Users with different emails; the filter is case-insensitive
        var scope = Scope(1);
        var ana = User(10, scope, "Ana", "ana@test.local");
        var bruno = User(11, scope, "Bruno", "bruno@test.local");
        var scopes = await ScopesWith(scope);
        var persons = await PersonsWith(ana, bruno);
        var handler = new ListScopePersonsQueryHandler(scopes, persons, Ownership(allowed: true));
        var query = QueryFor(scope);
        query.Email = "BRUNO@";

        // When
        var output = await handler.HandleAsync(query);

        // Then
        Assert.Equal(1, output.TotalItems);
        Assert.Equal(bruno.PublicId, Assert.Single(output.Data!).Id);
    }
}
