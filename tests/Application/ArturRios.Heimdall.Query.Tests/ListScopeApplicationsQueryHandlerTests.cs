using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Query.Handlers;
using ArturRios.Heimdall.Query.Input;
using ArturRios.Heimdall.Query.Input.Validation;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Heimdall.Shared.Services;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Mock;
using Moq;
using Application = ArturRios.Heimdall.Domain.Entities.Application;

namespace ArturRios.Heimdall.Query.Tests;

// Unit tests for ListScopeApplicationsQueryHandler (UC-17, FR-AP-05/FR-AP-09): a System Admin sees
// every application in the scope, a Scope Admin only the ones they own. Covers the main flow for
// both, a missing or logically deleted scope (AF-17a), a non-owning actor (AF-17b), the
// include-deleted behavior, the name and owner filters, and pagination.
public class ListScopeApplicationsQueryHandlerTests
{
    private static Scope Scope(long id, bool isDeleted = false) =>
        new() { Id = id, PublicId = Guid.NewGuid(), Name = $"scope-{id}", IsDeleted = isDeleted };

    private static Person Owner(long id, Scope scope) => new()
    {
        Id = id,
        PublicId = Guid.NewGuid(),
        Name = $"owner-{id}",
        Email = $"owner-{id}@test.local",
        RoleId = (long)Roles.ScopeAdmin,
        ScopeOwnerships = [new ScopeOwner { ScopeId = scope.Id, Scope = scope }]
    };

    private static Application App(
        long id, Scope scope, Person owner, string? name = null, bool isDeleted = false) => new()
    {
        Id = id,
        PublicId = Guid.NewGuid(),
        Name = name ?? $"app-{id}",
        IsDeleted = isDeleted,
        ScopeId = scope.Id,
        Scope = scope,
        OwnerId = owner.Id,
        Owner = owner,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
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

    private static async Task<AsyncFakeRepository<Application>> ApplicationsWith(params Application[] applications)
    {
        var repository = new AsyncFakeRepository<Application>();

        foreach (var application in applications)
        {
            await repository.CreateAsync(application);
        }

        return repository;
    }

    private static ListScopeApplicationsQuery QueryFor(
        Scope scope, int actingRole, Guid actingPersonId, int pageSize = 10) => new()
    {
        ScopeId = scope.PublicId,
        PageNumber = 1,
        PageSize = pageSize,
        ActingRole = actingRole,
        ActingPersonId = actingPersonId
    };

    [UnitFact]
    public async Task GivenSystemAdmin_WhenHandlingListScopeApplications_ThenEveryApplicationInTheScopeIsReturned()
    {
        // Given a scope whose two applications belong to different owners
        var scope = Scope(1);
        var owner = Owner(10, scope);
        var coOwner = Owner(11, scope);
        var first = App(100, scope, owner, "Alpha");
        var second = App(101, scope, coOwner, "Beta");
        var handler = new ListScopeApplicationsQueryHandler(
            await ScopesWith(scope), await ApplicationsWith(first, second), Ownership(allowed: true), new ListScopeApplicationsQueryValidator());

        // When
        var output = await handler.HandleAsync(QueryFor(scope, (int)Roles.SystemAdmin, Guid.NewGuid()));

        // Then
        Assert.True(output.Success);
        Assert.Equal(2, output.TotalItems);
        Assert.Equal([first.PublicId, second.PublicId], output.Data!.Select(x => x.Id));
        Assert.Contains(ApplicationMessages.ApplicationsRetrievedSuccessfully, output.Messages);
    }

    [UnitFact]
    public async Task GivenOwningScopeAdmin_WhenHandlingListScopeApplications_ThenOnlyTheirOwnAreReturned()
    {
        // Given two co-owners of one scope, each with an application
        var scope = Scope(1);
        var owner = Owner(10, scope);
        var coOwner = Owner(11, scope);
        var mine = App(100, scope, owner, "Alpha");
        var theirs = App(101, scope, coOwner, "Beta");
        var handler = new ListScopeApplicationsQueryHandler(
            await ScopesWith(scope), await ApplicationsWith(mine, theirs), Ownership(allowed: true), new ListScopeApplicationsQueryValidator());

        // When
        var output = await handler.HandleAsync(QueryFor(scope, (int)Roles.ScopeAdmin, owner.PublicId));

        // Then — a co-owner's application is not visible
        Assert.True(output.Success);
        Assert.Equal(1, output.TotalItems);
        Assert.Equal(mine.PublicId, output.Data!.Single().Id);
    }

    [UnitFact]
    public async Task GivenApplicationsOfAnotherScope_WhenHandlingListScopeApplications_ThenTheyAreNotReturned()
    {
        // Given an application in a different scope entirely
        var scope = Scope(1);
        var otherScope = Scope(2);
        var owner = Owner(10, scope);
        var outsideOwner = Owner(20, otherScope);
        var inside = App(100, scope, owner);
        var outside = App(200, otherScope, outsideOwner);
        var handler = new ListScopeApplicationsQueryHandler(
            await ScopesWith(scope, otherScope), await ApplicationsWith(inside, outside),
            Ownership(allowed: true), new ListScopeApplicationsQueryValidator());

        // When
        var output = await handler.HandleAsync(QueryFor(scope, (int)Roles.SystemAdmin, Guid.NewGuid()));

        // Then
        Assert.Equal(1, output.TotalItems);
        Assert.Equal(inside.PublicId, output.Data!.Single().Id);
    }

    [UnitFact]
    public async Task GivenMissingScope_WhenHandlingListScopeApplications_ThenScopeNotFoundIsReported()
    {
        // Given an empty scope store (AF-17a)
        var scope = Scope(1);
        var handler = new ListScopeApplicationsQueryHandler(
            await ScopesWith(), await ApplicationsWith(), Ownership(allowed: true), new ListScopeApplicationsQueryValidator());

        // When
        var output = await handler.HandleAsync(QueryFor(scope, (int)Roles.SystemAdmin, Guid.NewGuid()));

        // Then
        Assert.False(output.Success);
        Assert.Contains(ApplicationMessages.ScopeNotFound, output.Errors);
    }

    [UnitFact]
    public async Task GivenLogicallyDeletedScope_WhenHandlingListScopeApplications_ThenScopeNotFoundIsReported()
    {
        // Given a logically deleted scope (AF-17a)
        var scope = Scope(1, isDeleted: true);
        var handler = new ListScopeApplicationsQueryHandler(
            await ScopesWith(scope), await ApplicationsWith(), Ownership(allowed: true), new ListScopeApplicationsQueryValidator());

        // When
        var output = await handler.HandleAsync(QueryFor(scope, (int)Roles.SystemAdmin, Guid.NewGuid()));

        // Then
        Assert.False(output.Success);
        Assert.Contains(ApplicationMessages.ScopeNotFound, output.Errors);
    }

    [UnitFact]
    public async Task GivenScopeAdminWhoDoesNotOwnTheScope_WhenHandlingListScopeApplications_ThenNotScopeOwnerIsReported()
    {
        // Given the ownership checker rejects the actor (AF-17b). The refusal is distinct from an
        // empty page: it says the caller has no standing in this scope at all.
        var scope = Scope(1);
        var owner = Owner(10, scope);
        var application = App(100, scope, owner);
        var handler = new ListScopeApplicationsQueryHandler(
            await ScopesWith(scope), await ApplicationsWith(application), Ownership(allowed: false), new ListScopeApplicationsQueryValidator());

        // When
        var output = await handler.HandleAsync(QueryFor(scope, (int)Roles.ScopeAdmin, Guid.NewGuid()));

        // Then
        Assert.False(output.Success);
        Assert.Contains(ApplicationMessages.NotScopeOwner, output.Errors);
    }

    [UnitFact]
    public async Task GivenDeletedApplications_WhenHandlingListScopeApplications_ThenTheyAreExcludedByDefault()
    {
        // Given one active and one logically deleted application (FR-AP-09)
        var scope = Scope(1);
        var owner = Owner(10, scope);
        var active = App(100, scope, owner, "Alpha");
        var deleted = App(101, scope, owner, "Beta", isDeleted: true);
        var handler = new ListScopeApplicationsQueryHandler(
            await ScopesWith(scope), await ApplicationsWith(active, deleted), Ownership(allowed: true), new ListScopeApplicationsQueryValidator());

        // When
        var output = await handler.HandleAsync(QueryFor(scope, (int)Roles.SystemAdmin, Guid.NewGuid()));

        // Then
        Assert.Equal(1, output.TotalItems);
        Assert.Equal(active.PublicId, output.Data!.Single().Id);
    }

    [UnitFact]
    public async Task GivenIncludeDeleted_WhenHandlingListScopeApplications_ThenDeletedApplicationsAreReturned()
    {
        // Given deleted applications explicitly requested (FR-AP-09)
        var scope = Scope(1);
        var owner = Owner(10, scope);
        var active = App(100, scope, owner, "Alpha");
        var deleted = App(101, scope, owner, "Beta", isDeleted: true);
        var handler = new ListScopeApplicationsQueryHandler(
            await ScopesWith(scope), await ApplicationsWith(active, deleted), Ownership(allowed: true), new ListScopeApplicationsQueryValidator());

        var query = QueryFor(scope, (int)Roles.SystemAdmin, Guid.NewGuid());
        query.IncludeDeleted = true;

        // When
        var output = await handler.HandleAsync(query);

        // Then
        Assert.Equal(2, output.TotalItems);
    }

    [UnitFact]
    public async Task GivenNameFilter_WhenHandlingListScopeApplications_ThenOnlyMatchingApplicationsAreReturned()
    {
        // Given a case-insensitive substring filter
        var scope = Scope(1);
        var owner = Owner(10, scope);
        var billing = App(100, scope, owner, "Billing Service");
        var reporting = App(101, scope, owner, "Reporting Service");
        var handler = new ListScopeApplicationsQueryHandler(
            await ScopesWith(scope), await ApplicationsWith(billing, reporting), Ownership(allowed: true), new ListScopeApplicationsQueryValidator());

        var query = QueryFor(scope, (int)Roles.SystemAdmin, Guid.NewGuid());
        query.Name = "BILLING";

        // When
        var output = await handler.HandleAsync(query);

        // Then
        Assert.Equal(1, output.TotalItems);
        Assert.Equal(billing.PublicId, output.Data!.Single().Id);
    }

    [UnitFact]
    public async Task GivenOwnerFilter_WhenHandlingListScopeApplications_ThenOnlyThatOwnersApplicationsAreReturned()
    {
        // Given a System Admin narrowing the scope to one owner
        var scope = Scope(1);
        var owner = Owner(10, scope);
        var coOwner = Owner(11, scope);
        var theirs = App(100, scope, coOwner, "Alpha");
        var wanted = App(101, scope, owner, "Beta");
        var handler = new ListScopeApplicationsQueryHandler(
            await ScopesWith(scope), await ApplicationsWith(theirs, wanted), Ownership(allowed: true), new ListScopeApplicationsQueryValidator());

        var query = QueryFor(scope, (int)Roles.SystemAdmin, Guid.NewGuid());
        query.OwnerId = owner.PublicId;

        // When
        var output = await handler.HandleAsync(query);

        // Then
        Assert.Equal(1, output.TotalItems);
        Assert.Equal(wanted.PublicId, output.Data!.Single().Id);
    }

    [UnitFact]
    public async Task GivenPageSize_WhenHandlingListScopeApplications_ThenResultsArePagedByName()
    {
        // Given three applications and a page size of two, ordered by name
        var scope = Scope(1);
        var owner = Owner(10, scope);
        var charlie = App(100, scope, owner, "Charlie");
        var alpha = App(101, scope, owner, "Alpha");
        var bravo = App(102, scope, owner, "Bravo");
        var handler = new ListScopeApplicationsQueryHandler(
            await ScopesWith(scope), await ApplicationsWith(charlie, alpha, bravo), Ownership(allowed: true), new ListScopeApplicationsQueryValidator());

        // When
        var output = await handler.HandleAsync(
            QueryFor(scope, (int)Roles.SystemAdmin, Guid.NewGuid(), pageSize: 2));

        // Then — the first page holds the two alphabetically first
        Assert.Equal(3, output.TotalItems);
        Assert.Equal([alpha.PublicId, bravo.PublicId], output.Data!.Select(x => x.Id));
    }
}
