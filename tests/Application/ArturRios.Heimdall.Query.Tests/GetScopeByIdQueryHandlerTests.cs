using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Query.Handlers;
using ArturRios.Heimdall.Query.Input;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Mock;

namespace ArturRios.Heimdall.Query.Tests;

// Unit tests for GetScopeByIdQueryHandler (UC-02).
// Cover the main flow for each actor the use case allows — a System Admin sees any scope, a Scope
// Admin only the scopes they own, a User only the scope they belong to — plus AF-02a (scope not
// found), AF-02b (caller may not view the scope), and the include-deleted behavior (FR-SC-07).
public class GetScopeByIdQueryHandlerTests
{
    private static async Task<AsyncFakeRepository<Scope>> RepositoryWith(params Scope[] scopes)
    {
        var repository = new AsyncFakeRepository<Scope>();

        foreach (var scope in scopes)
        {
            await repository.CreateAsync(scope);
        }

        return repository;
    }

    private static Scope ScopeWithOwner(
        Guid publicId, string name, Guid ownerPublicId, Guid? memberPublicId = null, bool isDeleted = false) => new()
    {
        PublicId = publicId,
        Name = name,
        Description = "A scope",
        IsDeleted = isDeleted,
        Owners = [new ScopeOwner { Person = new Person { PublicId = ownerPublicId } }],
        Users = memberPublicId is null
            ? []
            : [new ScopeUser { Person = new Person { PublicId = memberPublicId.Value } }]
    };

    [UnitFact]
    public async Task GivenSystemAdminActor_WhenHandlingGetById_ThenAnyScopeIsReturned()
    {
        // Given a scope the acting System Admin neither owns nor belongs to
        var id = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var repository = await RepositoryWith(ScopeWithOwner(id, "Acme", ownerId));
        var handler = new GetScopeByIdQueryHandler(repository);

        // When
        var output = await handler.HandleAsync(new GetScopeByIdQuery
        {
            Id = id, ActingPersonId = Guid.NewGuid(), ActingRole = (int)Roles.SystemAdmin
        });

        // Then
        Assert.True(output.Success);
        Assert.NotNull(output.Data);
        Assert.Equal(id, output.Data!.Id);
        Assert.Equal("Acme", output.Data.Name);
        Assert.Equal([ownerId], output.Data.OwnerIds);
        Assert.Contains(ScopeMessages.ScopeRetrievedSuccessfully, output.Messages);
    }

    [UnitFact]
    public async Task GivenScopeAdminOwningScope_WhenHandlingGetById_ThenScopeIsReturned()
    {
        // Given a Scope Admin who owns the requested scope
        var id = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var repository = await RepositoryWith(ScopeWithOwner(id, "Acme", ownerId));
        var handler = new GetScopeByIdQueryHandler(repository);

        // When
        var output = await handler.HandleAsync(new GetScopeByIdQuery
        {
            Id = id, ActingPersonId = ownerId, ActingRole = (int)Roles.ScopeAdmin
        });

        // Then
        Assert.True(output.Success);
        Assert.Equal(id, output.Data!.Id);
    }

    [UnitFact]
    public async Task GivenScopeAdminNotOwningScope_WhenHandlingGetById_ThenReturnsNotAuthorizedToViewScope()
    {
        // Given a Scope Admin who owns some other scope (AF-02b)
        var id = Guid.NewGuid();
        var repository = await RepositoryWith(ScopeWithOwner(id, "Acme", Guid.NewGuid()));
        var handler = new GetScopeByIdQueryHandler(repository);

        // When
        var output = await handler.HandleAsync(new GetScopeByIdQuery
        {
            Id = id, ActingPersonId = Guid.NewGuid(), ActingRole = (int)Roles.ScopeAdmin
        });

        // Then
        Assert.False(output.Success);
        Assert.Null(output.Data);
        Assert.Contains(ScopeMessages.NotAuthorizedToViewScope, output.Errors);
    }

    [UnitFact]
    public async Task GivenUserBelongingToScope_WhenHandlingGetById_ThenScopeIsReturned()
    {
        // Given a User who belongs to the requested scope
        var id = Guid.NewGuid();
        var memberId = Guid.NewGuid();
        var repository = await RepositoryWith(ScopeWithOwner(id, "Acme", Guid.NewGuid(), memberId));
        var handler = new GetScopeByIdQueryHandler(repository);

        // When
        var output = await handler.HandleAsync(new GetScopeByIdQuery
        {
            Id = id, ActingPersonId = memberId, ActingRole = (int)Roles.User
        });

        // Then
        Assert.True(output.Success);
        Assert.Equal(id, output.Data!.Id);
    }

    [UnitFact]
    public async Task GivenUserOfAnotherScope_WhenHandlingGetById_ThenReturnsNotAuthorizedToViewScope()
    {
        // Given a User who belongs to a different scope than the one requested (AF-02b)
        var id = Guid.NewGuid();
        var repository = await RepositoryWith(ScopeWithOwner(id, "Acme", Guid.NewGuid(), Guid.NewGuid()));
        var handler = new GetScopeByIdQueryHandler(repository);

        // When
        var output = await handler.HandleAsync(new GetScopeByIdQuery
        {
            Id = id, ActingPersonId = Guid.NewGuid(), ActingRole = (int)Roles.User
        });

        // Then
        Assert.False(output.Success);
        Assert.Contains(ScopeMessages.NotAuthorizedToViewScope, output.Errors);
    }

    [UnitFact]
    public async Task GivenUnrecognizedRole_WhenHandlingGetById_ThenReturnsNotAuthorizedToViewScope()
    {
        // Given an actor whose role is none of the three defined ones — the rule denies by default
        var id = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var repository = await RepositoryWith(ScopeWithOwner(id, "Acme", ownerId, ownerId));
        var handler = new GetScopeByIdQueryHandler(repository);

        // When
        var output = await handler.HandleAsync(new GetScopeByIdQuery
        {
            Id = id, ActingPersonId = ownerId, ActingRole = 0
        });

        // Then
        Assert.False(output.Success);
        Assert.Contains(ScopeMessages.NotAuthorizedToViewScope, output.Errors);
    }

    [UnitFact]
    public async Task GivenMissingScope_WhenHandlingGetById_ThenReturnsScopeNotFound()
    {
        // Given an empty store (AF-02a)
        var repository = await RepositoryWith();
        var handler = new GetScopeByIdQueryHandler(repository);

        // When
        var output = await handler.HandleAsync(new GetScopeByIdQuery
        {
            Id = Guid.NewGuid(), ActingPersonId = Guid.NewGuid(), ActingRole = (int)Roles.SystemAdmin
        });

        // Then
        Assert.False(output.Success);
        Assert.Contains(ScopeMessages.ScopeNotFound, output.Errors);
    }

    [UnitFact]
    public async Task GivenMissingScopeAndNonAdminActor_WhenHandlingGetById_ThenReturnsScopeNotFound()
    {
        // Given an empty store and a User actor — AF-02a is decided before AF-02b, so a scope that
        // does not exist is not found rather than forbidden
        var repository = await RepositoryWith();
        var handler = new GetScopeByIdQueryHandler(repository);

        // When
        var output = await handler.HandleAsync(new GetScopeByIdQuery
        {
            Id = Guid.NewGuid(), ActingPersonId = Guid.NewGuid(), ActingRole = (int)Roles.User
        });

        // Then
        Assert.False(output.Success);
        Assert.Contains(ScopeMessages.ScopeNotFound, output.Errors);
        Assert.DoesNotContain(ScopeMessages.NotAuthorizedToViewScope, output.Errors);
    }

    [UnitFact]
    public async Task GivenDeletedScopeAndIncludeDeletedFalse_WhenHandlingGetById_ThenReturnsScopeNotFound()
    {
        // Given a logically deleted scope
        var id = Guid.NewGuid();
        var repository = await RepositoryWith(ScopeWithOwner(id, "Acme", Guid.NewGuid(), isDeleted: true));
        var handler = new GetScopeByIdQueryHandler(repository);

        // When
        var output = await handler.HandleAsync(new GetScopeByIdQuery
        {
            Id = id, IncludeDeleted = false,
            ActingPersonId = Guid.NewGuid(), ActingRole = (int)Roles.SystemAdmin
        });

        // Then
        Assert.False(output.Success);
        Assert.Contains(ScopeMessages.ScopeNotFound, output.Errors);
    }

    [UnitFact]
    public async Task GivenDeletedScopeAndIncludeDeletedTrue_WhenHandlingGetById_ThenScopeIsReturned()
    {
        // Given a logically deleted scope
        var id = Guid.NewGuid();
        var repository = await RepositoryWith(ScopeWithOwner(id, "Acme", Guid.NewGuid(), isDeleted: true));
        var handler = new GetScopeByIdQueryHandler(repository);

        // When
        var output = await handler.HandleAsync(new GetScopeByIdQuery
        {
            Id = id, IncludeDeleted = true,
            ActingPersonId = Guid.NewGuid(), ActingRole = (int)Roles.SystemAdmin
        });

        // Then
        Assert.True(output.Success);
        Assert.NotNull(output.Data);
        Assert.True(output.Data!.IsDeleted);
    }
}
