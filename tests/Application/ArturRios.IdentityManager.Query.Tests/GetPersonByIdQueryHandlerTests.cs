using ArturRios.IdentityManager.Domain.Entities;
using ArturRios.IdentityManager.Domain.Enums;
using ArturRios.IdentityManager.Query.Handlers;
using ArturRios.IdentityManager.Query.Input;
using ArturRios.IdentityManager.Shared.Messages;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Mock;

namespace ArturRios.IdentityManager.Query.Tests;

// Unit tests for GetPersonByIdQueryHandler (UC-07, FR-PE-03/FR-PE-08). Cover the main flow for each
// actor the use case allows, AF-07a (person not found, including logically deleted), AF-07b (caller
// may not view the person), and the include-deleted behavior.
public class GetPersonByIdQueryHandlerTests
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
        ScopeMembership = new ScopeUser { ScopeId = scope.Id, Scope = scope }
    };

    private static Person ScopeAdmin(long id, params Scope[] ownedScopes) => new()
    {
        Id = id,
        PublicId = Guid.NewGuid(),
        Name = $"admin-{id}",
        Email = $"admin-{id}@test.local",
        RoleId = (long)Roles.ScopeAdmin,
        ScopeOwnerships = ownedScopes
            .Select(scope => new ScopeOwner { ScopeId = scope.Id, Scope = scope })
            .ToList()
    };

    private static Person SystemAdmin(long id) => new()
    {
        Id = id,
        PublicId = Guid.NewGuid(),
        Name = $"root-{id}",
        Email = $"root-{id}@test.local",
        RoleId = (long)Roles.SystemAdmin
    };

    private static async Task<AsyncFakeRepository<Person>> RepositoryWith(params Person[] persons)
    {
        var repository = new AsyncFakeRepository<Person>();

        foreach (var person in persons)
        {
            await repository.CreateAsync(person);
        }

        return repository;
    }

    [UnitFact]
    public async Task GivenSystemAdminActor_WhenHandlingGetPersonById_ThenAnyPersonIsReturned()
    {
        // Given
        var scope = Scope(1);
        var target = User(10, scope);
        var repository = await RepositoryWith(target);
        var handler = new GetPersonByIdQueryHandler(repository);

        // When
        var output = await handler.HandleAsync(new GetPersonByIdQuery
        {
            Id = target.PublicId, ActingPersonId = Guid.NewGuid(), ActingRole = (int)Roles.SystemAdmin
        });

        // Then
        Assert.True(output.Success);
        Assert.NotNull(output.Data);
        Assert.Equal(target.PublicId, output.Data!.Id);
        Assert.Equal((int)Roles.User, output.Data.Role);
        Assert.Equal(scope.PublicId, output.Data.ScopeId);
        Assert.Contains(PersonMessages.PersonRetrievedSuccessfully, output.Messages);
    }

    [UnitFact]
    public async Task GivenUserActorRequestingSelf_WhenHandlingGetPersonById_ThenPersonIsReturned()
    {
        // Given
        var scope = Scope(1);
        var target = User(10, scope);
        var repository = await RepositoryWith(target);
        var handler = new GetPersonByIdQueryHandler(repository);

        // When
        var output = await handler.HandleAsync(new GetPersonByIdQuery
        {
            Id = target.PublicId, ActingPersonId = target.PublicId, ActingRole = (int)Roles.User
        });

        // Then
        Assert.True(output.Success);
        Assert.Equal(target.PublicId, output.Data!.Id);
    }

    [UnitFact]
    public async Task GivenUserActorRequestingAnotherPerson_WhenHandlingGetPersonById_ThenReturnsNotAuthorized()
    {
        // Given two Users in the same scope (AF-07b)
        var scope = Scope(1);
        var actor = User(10, scope);
        var target = User(11, scope);
        var repository = await RepositoryWith(actor, target);
        var handler = new GetPersonByIdQueryHandler(repository);

        // When
        var output = await handler.HandleAsync(new GetPersonByIdQuery
        {
            Id = target.PublicId, ActingPersonId = actor.PublicId, ActingRole = (int)Roles.User
        });

        // Then
        Assert.False(output.Success);
        Assert.Contains(PersonMessages.NotAuthorizedToViewPerson, output.Errors);
    }

    [UnitFact]
    public async Task GivenScopeAdminOwningTargetScope_WhenHandlingGetPersonById_ThenUserIsReturned()
    {
        // Given a ScopeAdmin who owns the scope the target User belongs to
        var scope = Scope(1);
        var actor = ScopeAdmin(10, scope);
        var target = User(11, scope);
        var repository = await RepositoryWith(actor, target);
        var handler = new GetPersonByIdQueryHandler(repository);

        // When
        var output = await handler.HandleAsync(new GetPersonByIdQuery
        {
            Id = target.PublicId, ActingPersonId = actor.PublicId, ActingRole = (int)Roles.ScopeAdmin
        });

        // Then
        Assert.True(output.Success);
        Assert.Equal(target.PublicId, output.Data!.Id);
    }

    [UnitFact]
    public async Task GivenDeletedScopeAdminActor_WhenHandlingGetPersonById_ThenReturnsNotAuthorized()
    {
        // Given a logically deleted ScopeAdmin who still owns the target User's scope. They can no
        // longer authenticate (UC-11 AF-11c), so a token issued before their deletion must not keep
        // granting reads.
        var scope = Scope(1);
        var actor = ScopeAdmin(10, scope);
        actor.IsDeleted = true;
        var target = User(11, scope);
        var repository = await RepositoryWith(actor, target);
        var handler = new GetPersonByIdQueryHandler(repository);

        // When
        var output = await handler.HandleAsync(new GetPersonByIdQuery
        {
            Id = target.PublicId, ActingPersonId = actor.PublicId, ActingRole = (int)Roles.ScopeAdmin
        });

        // Then
        Assert.False(output.Success);
        Assert.Contains(PersonMessages.NotAuthorizedToViewPerson, output.Errors);
    }

    [UnitFact]
    public async Task GivenScopeAdminNotOwningTargetScope_WhenHandlingGetPersonById_ThenReturnsNotAuthorized()
    {
        // Given a ScopeAdmin who owns a different scope than the target User's (AF-07b)
        var ownedScope = Scope(1);
        var otherScope = Scope(2);
        var actor = ScopeAdmin(10, ownedScope);
        var target = User(11, otherScope);
        var repository = await RepositoryWith(actor, target);
        var handler = new GetPersonByIdQueryHandler(repository);

        // When
        var output = await handler.HandleAsync(new GetPersonByIdQuery
        {
            Id = target.PublicId, ActingPersonId = actor.PublicId, ActingRole = (int)Roles.ScopeAdmin
        });

        // Then
        Assert.False(output.Success);
        Assert.Contains(PersonMessages.NotAuthorizedToViewPerson, output.Errors);
    }

    [UnitFact]
    public async Task GivenScopeAdminCoOwningScope_WhenHandlingGetPersonById_ThenOtherOwnerIsReturned()
    {
        // Given two ScopeAdmins owning the same scope
        var scope = Scope(1);
        var actor = ScopeAdmin(10, scope);
        var target = ScopeAdmin(11, scope);
        var repository = await RepositoryWith(actor, target);
        var handler = new GetPersonByIdQueryHandler(repository);

        // When
        var output = await handler.HandleAsync(new GetPersonByIdQuery
        {
            Id = target.PublicId, ActingPersonId = actor.PublicId, ActingRole = (int)Roles.ScopeAdmin
        });

        // Then
        Assert.True(output.Success);
        Assert.Equal(target.PublicId, output.Data!.Id);
        Assert.Equal([scope.PublicId], output.Data.OwnedScopeIds);
    }

    [UnitFact]
    public async Task GivenScopeAdminRequestingSystemAdmin_WhenHandlingGetPersonById_ThenReturnsNotAuthorized()
    {
        // Given a ScopeAdmin and an unrelated SystemAdmin (AF-07b)
        var scope = Scope(1);
        var actor = ScopeAdmin(10, scope);
        var target = SystemAdmin(11);
        var repository = await RepositoryWith(actor, target);
        var handler = new GetPersonByIdQueryHandler(repository);

        // When
        var output = await handler.HandleAsync(new GetPersonByIdQuery
        {
            Id = target.PublicId, ActingPersonId = actor.PublicId, ActingRole = (int)Roles.ScopeAdmin
        });

        // Then
        Assert.False(output.Success);
        Assert.Contains(PersonMessages.NotAuthorizedToViewPerson, output.Errors);
    }

    [UnitFact]
    public async Task GivenUnknownPersonId_WhenHandlingGetPersonById_ThenReturnsPersonNotFound()
    {
        // Given an empty store (AF-07a)
        var repository = await RepositoryWith();
        var handler = new GetPersonByIdQueryHandler(repository);

        // When
        var output = await handler.HandleAsync(new GetPersonByIdQuery
        {
            Id = Guid.NewGuid(), ActingPersonId = Guid.NewGuid(), ActingRole = (int)Roles.SystemAdmin
        });

        // Then
        Assert.False(output.Success);
        Assert.Contains(PersonMessages.PersonNotFound, output.Errors);
    }

    [UnitFact]
    public async Task GivenDeletedPersonAndIncludeDeletedFalse_WhenHandlingGetPersonById_ThenReturnsPersonNotFound()
    {
        // Given a logically deleted person (FR-PE-08)
        var scope = Scope(1);
        var target = User(10, scope, isDeleted: true);
        var repository = await RepositoryWith(target);
        var handler = new GetPersonByIdQueryHandler(repository);

        // When
        var output = await handler.HandleAsync(new GetPersonByIdQuery
        {
            Id = target.PublicId, IncludeDeleted = false,
            ActingPersonId = Guid.NewGuid(), ActingRole = (int)Roles.SystemAdmin
        });

        // Then
        Assert.False(output.Success);
        Assert.Contains(PersonMessages.PersonNotFound, output.Errors);
    }

    [UnitFact]
    public async Task GivenDeletedPersonAndIncludeDeletedTrue_WhenHandlingGetPersonById_ThenPersonIsReturned()
    {
        // Given a logically deleted person (FR-PE-08)
        var scope = Scope(1);
        var target = User(10, scope, isDeleted: true);
        var repository = await RepositoryWith(target);
        var handler = new GetPersonByIdQueryHandler(repository);

        // When
        var output = await handler.HandleAsync(new GetPersonByIdQuery
        {
            Id = target.PublicId, IncludeDeleted = true,
            ActingPersonId = Guid.NewGuid(), ActingRole = (int)Roles.SystemAdmin
        });

        // Then
        Assert.True(output.Success);
        Assert.True(output.Data!.IsDeleted);
    }
}
