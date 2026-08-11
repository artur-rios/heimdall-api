using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Query.Handlers;
using ArturRios.Heimdall.Query.Input;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Mock;
using Application = ArturRios.Heimdall.Domain.Entities.Application;

namespace ArturRios.Heimdall.Query.Tests;

// Unit tests for GetApplicationByIdQueryHandler (UC-17, FR-AP-04/FR-AP-09): a System Admin reads any
// application, a Scope Admin only the ones they own. Covers the main flow, AF-17a (unknown id, wrong
// scope, unknown scope, logically deleted), AF-17b (a Scope Admin who owns the scope but not the
// application, and an unrelated one), and the include-deleted behavior.
public class GetApplicationByIdQueryHandlerTests
{
    private static Scope Scope(long id) =>
        new() { Id = id, PublicId = Guid.NewGuid(), Name = $"scope-{id}" };

    private static Person Owner(long id, Scope scope) => new()
    {
        Id = id,
        PublicId = Guid.NewGuid(),
        Name = $"owner-{id}",
        Email = $"owner-{id}@test.local",
        RoleId = (long)Roles.ScopeAdmin,
        ScopeOwnerships = [new ScopeOwner { ScopeId = scope.Id, Scope = scope }]
    };

    private static Application App(long id, Scope scope, Person owner, bool isDeleted = false) => new()
    {
        Id = id,
        PublicId = Guid.NewGuid(),
        Name = $"app-{id}",
        IsDeleted = isDeleted,
        ScopeId = scope.Id,
        Scope = scope,
        OwnerId = owner.Id,
        Owner = owner,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    private static async Task<AsyncFakeRepository<Application>> ApplicationsWith(params Application[] applications)
    {
        var repository = new AsyncFakeRepository<Application>();

        foreach (var application in applications)
        {
            await repository.CreateAsync(application);
        }

        return repository;
    }

    private static GetApplicationByIdQuery QueryFor(
        Scope scope, Application application, int actingRole, Guid actingPersonId,
        bool includeDeleted = false) => new()
    {
        ScopeId = scope.PublicId,
        Id = application.PublicId,
        IncludeDeleted = includeDeleted,
        ActingRole = actingRole,
        ActingPersonId = actingPersonId
    };

    [UnitFact]
    public async Task GivenSystemAdmin_WhenHandlingGetApplicationById_ThenApplicationIsReturned()
    {
        // Given an application a System Admin does not own
        var scope = Scope(1);
        var owner = Owner(10, scope);
        var application = App(100, scope, owner);
        var handler = new GetApplicationByIdQueryHandler(await ApplicationsWith(application));

        // When
        var output = await handler.HandleAsync(
            QueryFor(scope, application, (int)Roles.SystemAdmin, Guid.NewGuid()));

        // Then
        Assert.True(output.Success);
        Assert.Equal(application.PublicId, output.Data!.Id);
        Assert.Contains(ApplicationMessages.ApplicationRetrievedSuccessfully, output.Messages);
    }

    [UnitFact]
    public async Task GivenOwningScopeAdmin_WhenHandlingGetApplicationById_ThenApplicationIsReturned()
    {
        // Given the acting Scope Admin owns the application
        var scope = Scope(1);
        var owner = Owner(10, scope);
        var application = App(100, scope, owner);
        var handler = new GetApplicationByIdQueryHandler(await ApplicationsWith(application));

        // When
        var output = await handler.HandleAsync(
            QueryFor(scope, application, (int)Roles.ScopeAdmin, owner.PublicId));

        // Then
        Assert.True(output.Success);
        Assert.Equal(application.PublicId, output.Data!.Id);
    }

    [UnitFact]
    public async Task GivenReturnedApplication_WhenHandlingGetApplicationById_ThenOutputCarriesPublicIdentifiers()
    {
        // Given an application whose internal ids differ from its public ones
        var scope = Scope(7);
        var owner = Owner(70, scope);
        var application = App(700, scope, owner);
        var handler = new GetApplicationByIdQueryHandler(await ApplicationsWith(application));

        // When
        var output = await handler.HandleAsync(
            QueryFor(scope, application, (int)Roles.SystemAdmin, Guid.NewGuid()));

        // Then — the scope and owner are identified by PublicId, never by the bigint foreign keys
        Assert.Equal(scope.PublicId, output.Data!.ScopeId);
        Assert.Equal(owner.PublicId, output.Data.OwnerId);
        Assert.Equal(application.Name, output.Data.Name);
        Assert.False(output.Data.IsDeleted);
    }

    [UnitFact]
    public async Task GivenScopeAdminWhoOwnsTheScopeButNotTheApplication_WhenHandlingGetApplicationById_ThenNotAuthorizedIsReported()
    {
        // Given a co-owner of the scope who does not own this application: owning the scope is not by
        // itself grounds to read another owner's application (AF-17b)
        var scope = Scope(1);
        var owner = Owner(10, scope);
        var coOwner = Owner(11, scope);
        var application = App(100, scope, owner);
        var handler = new GetApplicationByIdQueryHandler(await ApplicationsWith(application));

        // When
        var output = await handler.HandleAsync(
            QueryFor(scope, application, (int)Roles.ScopeAdmin, coOwner.PublicId));

        // Then
        Assert.False(output.Success);
        Assert.Contains(ApplicationMessages.NotAuthorizedToViewApplication, output.Errors);
    }

    [UnitFact]
    public async Task GivenUnrelatedScopeAdmin_WhenHandlingGetApplicationById_ThenNotAuthorizedIsReported()
    {
        // Given a Scope Admin with nothing to do with the scope (AF-17b)
        var scope = Scope(1);
        var owner = Owner(10, scope);
        var application = App(100, scope, owner);
        var handler = new GetApplicationByIdQueryHandler(await ApplicationsWith(application));

        // When
        var output = await handler.HandleAsync(
            QueryFor(scope, application, (int)Roles.ScopeAdmin, Guid.NewGuid()));

        // Then
        Assert.False(output.Success);
        Assert.Contains(ApplicationMessages.NotAuthorizedToViewApplication, output.Errors);
    }

    [UnitFact]
    public async Task GivenUnknownApplication_WhenHandlingGetApplicationById_ThenApplicationNotFoundIsReported()
    {
        // Given an id nobody holds (AF-17a)
        var scope = Scope(1);
        var handler = new GetApplicationByIdQueryHandler(await ApplicationsWith());

        // When
        var output = await handler.HandleAsync(new GetApplicationByIdQuery
        {
            ScopeId = scope.PublicId,
            Id = Guid.NewGuid(),
            ActingRole = (int)Roles.SystemAdmin,
            ActingPersonId = Guid.NewGuid()
        });

        // Then
        Assert.False(output.Success);
        Assert.Contains(ApplicationMessages.ApplicationNotFound, output.Errors);
    }

    [UnitFact]
    public async Task GivenApplicationOfADifferentScope_WhenHandlingGetApplicationById_ThenApplicationNotFoundIsReported()
    {
        // Given the application exists, but under another scope than the one addressed (AF-17a)
        var scope = Scope(1);
        var otherScope = Scope(2);
        var owner = Owner(10, otherScope);
        var application = App(100, otherScope, owner);
        var handler = new GetApplicationByIdQueryHandler(await ApplicationsWith(application));

        // When
        var output = await handler.HandleAsync(
            QueryFor(scope, application, (int)Roles.SystemAdmin, Guid.NewGuid()));

        // Then
        Assert.False(output.Success);
        Assert.Contains(ApplicationMessages.ApplicationNotFound, output.Errors);
    }

    [UnitFact]
    public async Task GivenUnknownScope_WhenHandlingGetApplicationById_ThenApplicationNotFoundIsReported()
    {
        // Given a scope id nobody holds: the addressed resource does not exist either (AF-17a)
        var scope = Scope(1);
        var owner = Owner(10, scope);
        var application = App(100, scope, owner);
        var handler = new GetApplicationByIdQueryHandler(await ApplicationsWith(application));

        // When
        var output = await handler.HandleAsync(new GetApplicationByIdQuery
        {
            ScopeId = Guid.NewGuid(),
            Id = application.PublicId,
            ActingRole = (int)Roles.SystemAdmin,
            ActingPersonId = Guid.NewGuid()
        });

        // Then
        Assert.False(output.Success);
        Assert.Contains(ApplicationMessages.ApplicationNotFound, output.Errors);
    }

    [UnitFact]
    public async Task GivenDeletedApplicationAndIncludeDeletedFalse_WhenHandlingGetApplicationById_ThenApplicationNotFoundIsReported()
    {
        // Given a logically deleted application and no explicit request for it (FR-AP-09, AF-17a)
        var scope = Scope(1);
        var owner = Owner(10, scope);
        var application = App(100, scope, owner, isDeleted: true);
        var handler = new GetApplicationByIdQueryHandler(await ApplicationsWith(application));

        // When
        var output = await handler.HandleAsync(
            QueryFor(scope, application, (int)Roles.SystemAdmin, Guid.NewGuid()));

        // Then
        Assert.False(output.Success);
        Assert.Contains(ApplicationMessages.ApplicationNotFound, output.Errors);
    }

    [UnitFact]
    public async Task GivenDeletedApplicationAndIncludeDeletedTrue_WhenHandlingGetApplicationById_ThenApplicationIsReturned()
    {
        // Given a logically deleted application explicitly requested (FR-AP-09)
        var scope = Scope(1);
        var owner = Owner(10, scope);
        var application = App(100, scope, owner, isDeleted: true);
        var handler = new GetApplicationByIdQueryHandler(await ApplicationsWith(application));

        // When
        var output = await handler.HandleAsync(
            QueryFor(scope, application, (int)Roles.SystemAdmin, Guid.NewGuid(), includeDeleted: true));

        // Then
        Assert.True(output.Success);
        Assert.True(output.Data!.IsDeleted);
    }
}
