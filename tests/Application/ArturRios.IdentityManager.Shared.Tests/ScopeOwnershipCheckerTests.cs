using ArturRios.IdentityManager.Domain.Entities;
using ArturRios.IdentityManager.Domain.Enums;
using ArturRios.IdentityManager.Shared.Services;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Mock;

namespace ArturRios.IdentityManager.Shared.Tests;

// Unit tests for ScopeOwnershipChecker (UC-06 AF-06e / UC-07 AF-07b authorization): a System Admin
// always may act; any other actor must own the target scope (a SCOPE_OWNER row links their person
// id to it).
public class ScopeOwnershipCheckerTests
{
    [UnitFact]
    public async Task GivenSystemAdminActor_WhenCheckingScopeManagement_ThenAllowedWithoutOwnership()
    {
        // Given a store with no ownership rows for the actor
        var persons = new AsyncFakeRepository<Person>();
        var checker = new ScopeOwnershipChecker(persons);

        // When a System Admin (any person id, any scope) is checked
        var allowed = await checker.ActorMayManageScopeAsync((int)Roles.SystemAdmin, actingPersonId: Guid.NewGuid(), scopeId: 1);

        // Then
        Assert.True(allowed);
    }

    [UnitFact]
    public async Task GivenScopeAdminOwningScope_WhenCheckingScopeManagement_ThenAllowed()
    {
        // Given a ScopeAdmin who owns scope 1
        var persons = new AsyncFakeRepository<Person>();
        var actor = new Person
        {
            RoleId = (long)Roles.ScopeAdmin,
            ScopeOwnerships = [new ScopeOwner { ScopeId = 1 }]
        };
        await persons.CreateAsync(actor);
        var checker = new ScopeOwnershipChecker(persons);

        // When
        var allowed = await checker.ActorMayManageScopeAsync((int)Roles.ScopeAdmin, actor.PublicId, scopeId: 1);

        // Then
        Assert.True(allowed);
    }

    [UnitFact]
    public async Task GivenLogicallyDeletedScopeAdmin_WhenCheckingScopeManagement_ThenNotAllowed()
    {
        // Given a logically deleted ScopeAdmin who still has a SCOPE_OWNER row for scope 1. They can
        // no longer authenticate (UC-11 AF-11c), so a token issued before their deletion must not
        // keep working.
        var persons = new AsyncFakeRepository<Person>();
        var actor = new Person
        {
            RoleId = (long)Roles.ScopeAdmin,
            IsDeleted = true,
            ScopeOwnerships = [new ScopeOwner { ScopeId = 1 }]
        };
        await persons.CreateAsync(actor);
        var checker = new ScopeOwnershipChecker(persons);

        // When
        var allowed = await checker.ActorMayManageScopeAsync((int)Roles.ScopeAdmin, actor.PublicId, scopeId: 1);

        // Then
        Assert.False(allowed);
    }

    [UnitFact]
    public async Task GivenScopeAdminNotOwningScope_WhenCheckingScopeManagement_ThenNotAllowed()
    {
        // Given a ScopeAdmin who owns no scope
        var persons = new AsyncFakeRepository<Person>();
        var actor = new Person { RoleId = (long)Roles.ScopeAdmin };
        await persons.CreateAsync(actor);
        var checker = new ScopeOwnershipChecker(persons);

        // When checked against scope 1 (which they do not own)
        var allowed = await checker.ActorMayManageScopeAsync((int)Roles.ScopeAdmin, actor.PublicId, scopeId: 1);

        // Then
        Assert.False(allowed);
    }
}
