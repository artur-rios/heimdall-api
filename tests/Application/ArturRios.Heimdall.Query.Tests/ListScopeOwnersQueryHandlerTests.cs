using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Query.Handlers;
using ArturRios.Heimdall.Query.Input;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Heimdall.Shared.Services;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Mock;
using Moq;

namespace ArturRios.Heimdall.Query.Tests;

// Unit tests for ListScopeOwnersQueryHandler (UC-07, FR-PE-04/FR-PE-08): the scope's ScopeAdmin
// owners only, paginated and filterable, gated by scope ownership. Covers the main flow, a missing
// or logically deleted scope (AF-07a), a non-owning actor (AF-07b), and include-deleted behavior.
public class ListScopeOwnersQueryHandlerTests
{
    private static Scope Scope(long id, bool isDeleted = false) =>
        new() { Id = id, PublicId = Guid.NewGuid(), Name = $"scope-{id}", IsDeleted = isDeleted };

    private static Person Owner(long id, Scope scope, string name, string email, bool isDeleted = false) => new()
    {
        Id = id,
        PublicId = Guid.NewGuid(),
        Name = name,
        Email = email,
        RoleId = (long)Roles.ScopeAdmin,
        IsDeleted = isDeleted,
        ScopeOwnerships = [new ScopeOwner { ScopeId = scope.Id, Scope = scope }]
    };

    private static Person Member(long id, Scope scope) => new()
    {
        Id = id,
        PublicId = Guid.NewGuid(),
        Name = $"user-{id}",
        Email = $"user-{id}@test.local",
        RoleId = (long)Roles.User,
        ScopeMembership = new ScopeUser { ScopeId = scope.Id, Scope = scope }
    };

    private static IScopeOwnershipChecker Ownership(bool allowed)
    {
        var checker = new Mock<IScopeOwnershipChecker>();
        checker
            .Setup(x => x.ActorMayManageScopeAsync(It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<long>()))
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

    private static ListScopeOwnersQuery QueryFor(Scope scope) => new()
    {
        ScopeId = scope.PublicId,
        PageNumber = 1,
        PageSize = 10,
        ActingPersonId = Guid.NewGuid(),
        ActingRole = (int)Roles.SystemAdmin
    };

    [UnitFact]
    public async Task GivenScopeWithOwners_WhenHandlingListScopeOwners_ThenOnlyItsOwnersAreReturned()
    {
        // Given a scope with two owners, one User, and an owner of another scope
        var scope = Scope(1);
        var otherScope = Scope(2);
        var ana = Owner(10, scope, "Ana", "ana@test.local");
        var bruno = Owner(11, scope, "Bruno", "bruno@test.local");
        var member = Member(12, scope);
        var outsider = Owner(13, otherScope, "Carla", "carla@test.local");
        var scopes = await ScopesWith(scope, otherScope);
        var persons = await PersonsWith(ana, bruno, member, outsider);
        var handler = new ListScopeOwnersQueryHandler(scopes, persons, Ownership(allowed: true));

        // When
        var output = await handler.HandleAsync(QueryFor(scope));

        // Then
        Assert.True(output.Success);
        Assert.Equal(2, output.TotalItems);
        Assert.Equal([ana.PublicId, bruno.PublicId], output.Data!.Select(x => x.Id));
        Assert.Contains(PersonMessages.PersonsRetrievedSuccessfully, output.Messages);
    }

    [UnitFact]
    public async Task GivenUnknownScope_WhenHandlingListScopeOwners_ThenReturnsScopeNotFound()
    {
        // Given an empty scope store (AF-07a)
        var scopes = await ScopesWith();
        var persons = await PersonsWith();
        var handler = new ListScopeOwnersQueryHandler(scopes, persons, Ownership(allowed: true));

        // When
        var output = await handler.HandleAsync(new ListScopeOwnersQuery
        {
            ScopeId = Guid.NewGuid(), PageNumber = 1, PageSize = 10,
            ActingPersonId = Guid.NewGuid(), ActingRole = (int)Roles.SystemAdmin
        });

        // Then
        Assert.False(output.Success);
        Assert.Contains(PersonMessages.ScopeNotFound, output.Errors);
    }

    [UnitFact]
    public async Task GivenDeletedScope_WhenHandlingListScopeOwners_ThenReturnsScopeNotFound()
    {
        // Given a logically deleted scope (AF-07a)
        var scope = Scope(1, isDeleted: true);
        var scopes = await ScopesWith(scope);
        var persons = await PersonsWith();
        var handler = new ListScopeOwnersQueryHandler(scopes, persons, Ownership(allowed: true));

        // When
        var output = await handler.HandleAsync(QueryFor(scope));

        // Then
        Assert.False(output.Success);
        Assert.Contains(PersonMessages.ScopeNotFound, output.Errors);
    }

    [UnitFact]
    public async Task GivenActorNotOwningScope_WhenHandlingListScopeOwners_ThenReturnsNotScopeOwner()
    {
        // Given an actor the ownership checker rejects (AF-07b)
        var scope = Scope(1);
        var scopes = await ScopesWith(scope);
        var persons = await PersonsWith(Owner(10, scope, "Ana", "ana@test.local"));
        var handler = new ListScopeOwnersQueryHandler(scopes, persons, Ownership(allowed: false));

        // When
        var output = await handler.HandleAsync(QueryFor(scope));

        // Then
        Assert.False(output.Success);
        Assert.Contains(PersonMessages.NotScopeOwner, output.Errors);
    }

    [UnitFact]
    public async Task GivenDeletedOwnerAndIncludeDeletedFalse_WhenHandlingListScopeOwners_ThenOwnerIsExcluded()
    {
        // Given one active and one logically deleted owner (FR-PE-08)
        var scope = Scope(1);
        var active = Owner(10, scope, "Ana", "ana@test.local");
        var deleted = Owner(11, scope, "Bruno", "bruno@test.local", isDeleted: true);
        var scopes = await ScopesWith(scope);
        var persons = await PersonsWith(active, deleted);
        var handler = new ListScopeOwnersQueryHandler(scopes, persons, Ownership(allowed: true));

        // When
        var output = await handler.HandleAsync(QueryFor(scope));

        // Then
        Assert.Equal(1, output.TotalItems);
        Assert.Equal(active.PublicId, Assert.Single(output.Data!).Id);
    }

    [UnitFact]
    public async Task GivenDeletedOwnerAndIncludeDeletedTrue_WhenHandlingListScopeOwners_ThenOwnerIsIncluded()
    {
        // Given one active and one logically deleted owner (FR-PE-08)
        var scope = Scope(1);
        var active = Owner(10, scope, "Ana", "ana@test.local");
        var deleted = Owner(11, scope, "Bruno", "bruno@test.local", isDeleted: true);
        var scopes = await ScopesWith(scope);
        var persons = await PersonsWith(active, deleted);
        var handler = new ListScopeOwnersQueryHandler(scopes, persons, Ownership(allowed: true));
        var query = QueryFor(scope);
        query.IncludeDeleted = true;

        // When
        var output = await handler.HandleAsync(query);

        // Then
        Assert.Equal(2, output.TotalItems);
    }

    [UnitFact]
    public async Task GivenNameFilter_WhenHandlingListScopeOwners_ThenOnlyMatchingOwnersAreReturned()
    {
        // Given two owners with different names; the filter is case-insensitive
        var scope = Scope(1);
        var ana = Owner(10, scope, "Ana", "ana@test.local");
        var bruno = Owner(11, scope, "Bruno", "bruno@test.local");
        var scopes = await ScopesWith(scope);
        var persons = await PersonsWith(ana, bruno);
        var handler = new ListScopeOwnersQueryHandler(scopes, persons, Ownership(allowed: true));
        var query = QueryFor(scope);
        query.Name = "AN";

        // When
        var output = await handler.HandleAsync(query);

        // Then
        Assert.Equal(1, output.TotalItems);
        Assert.Equal(ana.PublicId, Assert.Single(output.Data!).Id);
    }

    [UnitFact]
    public async Task GivenEmailFilter_WhenHandlingListScopeOwners_ThenOnlyMatchingOwnersAreReturned()
    {
        // Given two owners with different emails; the filter is case-insensitive
        var scope = Scope(1);
        var ana = Owner(10, scope, "Ana", "ana@test.local");
        var bruno = Owner(11, scope, "Bruno", "bruno@test.local");
        var scopes = await ScopesWith(scope);
        var persons = await PersonsWith(ana, bruno);
        var handler = new ListScopeOwnersQueryHandler(scopes, persons, Ownership(allowed: true));
        var query = QueryFor(scope);
        query.Email = "BRUNO@";

        // When
        var output = await handler.HandleAsync(query);

        // Then
        Assert.Equal(1, output.TotalItems);
        Assert.Equal(bruno.PublicId, Assert.Single(output.Data!).Id);
    }
}
