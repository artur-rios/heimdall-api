using ArturRios.IdentityManager.Domain.Entities;
using ArturRios.IdentityManager.Query.Handlers;
using ArturRios.IdentityManager.Query.Input;
using ArturRios.IdentityManager.Shared.Messages;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Mock;

namespace ArturRios.IdentityManager.Query.Tests;

// Unit tests for ListScopesQueryHandler (UC-02).
// Cover the main flow (pagination + filtering, FR-SC-03) and the include-deleted behavior (FR-SC-07).
public class ListScopesQueryHandlerTests
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

    private static Scope NamedScope(string name, bool isDeleted = false) => new()
    {
        PublicId = Guid.NewGuid(),
        Name = name,
        IsDeleted = isDeleted,
        Owners = [new ScopeOwner { Person = new Person { PublicId = Guid.NewGuid() } }]
    };

    [UnitFact]
    public async Task GivenScopes_WhenHandlingList_ThenAllNonDeletedAreReturned()
    {
        // Given
        var repository = await RepositoryWith(NamedScope("Alpha"), NamedScope("Beta"));
        var handler = new ListScopesQueryHandler(repository);

        // When
        var output = await handler.HandleAsync(new ListScopesQuery { PageNumber = 1, PageSize = 10 });

        // Then
        Assert.True(output.Success);
        Assert.Equal(2, output.TotalItems);
        Assert.Equal(2, output.Data!.Count);
        Assert.Contains(ScopeMessages.ScopesRetrievedSuccessfully, output.Messages);
    }

    [UnitFact]
    public async Task GivenNameFilter_WhenHandlingList_ThenOnlyMatchingScopesAreReturned()
    {
        // Given
        var repository = await RepositoryWith(NamedScope("Alpha"), NamedScope("Beta"), NamedScope("Alphabet"));
        var handler = new ListScopesQueryHandler(repository);

        // When
        var output = await handler.HandleAsync(new ListScopesQuery { Name = "Alpha", PageNumber = 1, PageSize = 10 });

        // Then — "Alpha" and "Alphabet" match, "Beta" does not
        Assert.Equal(2, output.TotalItems);
        Assert.All(output.Data!, scope => Assert.Contains("Alpha", scope.Name));
    }

    [UnitFact]
    public async Task GivenNameFilterInDifferentCase_WhenHandlingList_ThenMatchingScopesAreStillReturned()
    {
        // Given scopes whose names differ from the filter only by case (the filter is
        // case-insensitive, as the person listings are)
        var repository = await RepositoryWith(NamedScope("Alpha"), NamedScope("Beta"), NamedScope("Alphabet"));
        var handler = new ListScopesQueryHandler(repository);

        // When
        var output = await handler.HandleAsync(new ListScopesQuery { Name = "aLpHa", PageNumber = 1, PageSize = 10 });

        // Then
        Assert.Equal(2, output.TotalItems);
        Assert.All(output.Data!, scope => Assert.Contains("Alpha", scope.Name));
    }

    [UnitFact]
    public async Task GivenDeletedScopeAndIncludeDeletedFalse_WhenHandlingList_ThenDeletedIsExcluded()
    {
        // Given one active and one deleted scope
        var repository = await RepositoryWith(NamedScope("Active"), NamedScope("Gone", isDeleted: true));
        var handler = new ListScopesQueryHandler(repository);

        // When
        var output = await handler.HandleAsync(new ListScopesQuery { PageNumber = 1, PageSize = 10 });

        // Then
        Assert.Equal(1, output.TotalItems);
        Assert.Equal("Active", Assert.Single(output.Data!).Name);
    }

    [UnitFact]
    public async Task GivenDeletedScopeAndIncludeDeletedTrue_WhenHandlingList_ThenDeletedIsIncluded()
    {
        // Given one active and one deleted scope
        var repository = await RepositoryWith(NamedScope("Active"), NamedScope("Gone", isDeleted: true));
        var handler = new ListScopesQueryHandler(repository);

        // When
        var output = await handler.HandleAsync(new ListScopesQuery { IncludeDeleted = true, PageNumber = 1, PageSize = 10 });

        // Then
        Assert.Equal(2, output.TotalItems);
    }
}
