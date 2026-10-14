using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Shared.Services;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Mock;
using Application = ArturRios.Heimdall.Domain.Entities.Application;

namespace ArturRios.Heimdall.Shared.Tests;

// Unit tests for ApplicationManagementAccess (UC-53…UC-55): the rule UC-17/UC-18 already apply to an
// application — a System Admin manages any; a Scope Admin only one they own and whose scope they
// still own — with "not found" (missing, in another scope, logically deleted, or its scope logically
// deleted) answered before authorization so both outcomes stay observable.
public class ApplicationManagementAccessTests
{
    private static async Task<(AsyncFakeRepository<Application, long> Applications, Application Application, Person Owner, Scope Scope)>
        SeedAsync(bool applicationDeleted = false, bool scopeDeleted = false, bool ownerStillOwnsScope = true)
    {
        var scope = new Scope { Id = 1, PublicId = Guid.NewGuid(), Name = "Acme", IsDeleted = scopeDeleted };
        var owner = new Person
        {
            Id = 1,
            PublicId = Guid.NewGuid(),
            Name = "Owner",
            Email = "owner@test.local",
            RoleId = (long)Roles.ScopeAdmin,
            ScopeOwnerships = ownerStillOwnsScope ? [new ScopeOwner { ScopeId = scope.Id, Scope = scope }] : []
        };
        var application = new Application
        {
            Id = 1,
            PublicId = Guid.NewGuid(),
            Name = "Billing Service",
            IsDeleted = applicationDeleted,
            ScopeId = scope.Id,
            Scope = scope,
            OwnerId = owner.Id,
            Owner = owner
        };
        var applications = new AsyncFakeRepository<Application, long>();
        await applications.CreateAsync(application);

        return (applications, application, owner, scope);
    }

    [UnitFact]
    public async Task GivenASystemAdmin_WhenResolving_ThenAccessIsGranted()
    {
        // Given
        var (applications, application, _, scope) = await SeedAsync();

        // When
        var result = await ApplicationManagementAccess.ResolveAsync(
            applications.Query(), scope.PublicId, application.PublicId, (int)Roles.SystemAdmin, Guid.NewGuid());

        // Then
        Assert.Equal(ApplicationAccess.Granted, result.Access);
        Assert.Same(application, result.Application);
    }

    [UnitFact]
    public async Task GivenTheOwningScopeAdmin_WhenResolving_ThenAccessIsGranted()
    {
        // Given
        var (applications, application, owner, scope) = await SeedAsync();

        // When
        var result = await ApplicationManagementAccess.ResolveAsync(
            applications.Query(), scope.PublicId, application.PublicId, (int)Roles.ScopeAdmin, owner.PublicId);

        // Then
        Assert.Equal(ApplicationAccess.Granted, result.Access);
    }

    [UnitFact]
    public async Task GivenAnotherScopeAdmin_WhenResolving_ThenAccessIsForbidden()
    {
        // Given — owning the scope is not owning the application
        var (applications, application, _, scope) = await SeedAsync();

        // When
        var result = await ApplicationManagementAccess.ResolveAsync(
            applications.Query(), scope.PublicId, application.PublicId, (int)Roles.ScopeAdmin, Guid.NewGuid());

        // Then
        Assert.Equal(ApplicationAccess.Forbidden, result.Access);
        Assert.Null(result.Application);
    }

    [UnitFact]
    public async Task GivenAnOwnerRemovedFromTheScope_WhenResolving_ThenAccessIsForbidden()
    {
        // Given — UC-22 took the scope away but left them named as owner (AF-17b)
        var (applications, application, owner, scope) = await SeedAsync(ownerStillOwnsScope: false);

        // When
        var result = await ApplicationManagementAccess.ResolveAsync(
            applications.Query(), scope.PublicId, application.PublicId, (int)Roles.ScopeAdmin, owner.PublicId);

        // Then
        Assert.Equal(ApplicationAccess.Forbidden, result.Access);
    }

    [UnitTheory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public async Task GivenNoLiveApplicationInTheScope_WhenResolving_ThenItIsNotFound(
        bool applicationDeleted, bool scopeDeleted, bool otherScope)
    {
        // Given — deleted application, deleted scope, or the route naming another scope
        var (applications, application, _, scope) = await SeedAsync(applicationDeleted, scopeDeleted);
        var routeScope = otherScope ? Guid.NewGuid() : scope.PublicId;

        // When — even a System Admin
        var result = await ApplicationManagementAccess.ResolveAsync(
            applications.Query(), routeScope, application.PublicId, (int)Roles.SystemAdmin, Guid.NewGuid());

        // Then
        Assert.Equal(ApplicationAccess.NotFound, result.Access);
        Assert.Null(result.Application);
    }
}
