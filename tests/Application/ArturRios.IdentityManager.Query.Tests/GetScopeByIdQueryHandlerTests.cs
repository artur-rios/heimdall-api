using ArturRios.IdentityManager.Domain.Entities;
using ArturRios.IdentityManager.Query.Handlers;
using ArturRios.IdentityManager.Query.Input;
using ArturRios.IdentityManager.Shared.Messages;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Mock;

namespace ArturRios.IdentityManager.Query.Tests;

// Unit tests for GetScopeByIdQueryHandler (UC-02).
// Cover the main flow (scope found) plus alternative flow AF-02a (scope not found), and the
// include-deleted behavior (FR-SC-07). AF-02b (not authorized) is a functional concern.
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

    private static Scope ScopeWithOwner(Guid publicId, string name, Guid ownerPublicId, bool isDeleted = false) => new()
    {
        PublicId = publicId,
        Name = name,
        Description = "A scope",
        IsDeleted = isDeleted,
        Owners = [new ScopeOwner { Person = new Person { PublicId = ownerPublicId } }]
    };

    [UnitFact]
    public async Task GivenExistingScope_WhenHandlingGetById_ThenScopeIsReturned()
    {
        // Given
        var id = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var repository = await RepositoryWith(ScopeWithOwner(id, "Acme", ownerId));
        var handler = new GetScopeByIdQueryHandler(repository);

        // When
        var output = await handler.HandleAsync(new GetScopeByIdQuery { Id = id });

        // Then
        Assert.True(output.Success);
        Assert.NotNull(output.Data);
        Assert.Equal(id, output.Data!.Id);
        Assert.Equal("Acme", output.Data.Name);
        Assert.Equal([ownerId], output.Data.OwnerIds);
        Assert.Contains(ScopeMessages.ScopeRetrievedSuccessfully, output.Messages);
    }

    [UnitFact]
    public async Task GivenMissingScope_WhenHandlingGetById_ThenReturnsScopeNotFound()
    {
        // Given an empty store
        var repository = await RepositoryWith();
        var handler = new GetScopeByIdQueryHandler(repository);

        // When
        var output = await handler.HandleAsync(new GetScopeByIdQuery { Id = Guid.NewGuid() });

        // Then
        Assert.False(output.Success);
        Assert.Contains(ScopeMessages.ScopeNotFound, output.Errors);
    }

    [UnitFact]
    public async Task GivenDeletedScopeAndIncludeDeletedFalse_WhenHandlingGetById_ThenReturnsScopeNotFound()
    {
        // Given a logically deleted scope
        var id = Guid.NewGuid();
        var repository = await RepositoryWith(ScopeWithOwner(id, "Acme", Guid.NewGuid(), isDeleted: true));
        var handler = new GetScopeByIdQueryHandler(repository);

        // When
        var output = await handler.HandleAsync(new GetScopeByIdQuery { Id = id, IncludeDeleted = false });

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
        var output = await handler.HandleAsync(new GetScopeByIdQuery { Id = id, IncludeDeleted = true });

        // Then
        Assert.True(output.Success);
        Assert.NotNull(output.Data);
        Assert.True(output.Data!.IsDeleted);
    }
}
