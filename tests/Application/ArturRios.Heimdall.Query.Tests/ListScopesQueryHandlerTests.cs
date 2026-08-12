using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Query.Handlers;
using ArturRios.Heimdall.Query.Input;
using ArturRios.Heimdall.Query.Input.Validation;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Mock;

namespace ArturRios.Heimdall.Query.Tests;

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
        var handler = new ListScopesQueryHandler(repository, new ListScopesQueryValidator());

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
        var handler = new ListScopesQueryHandler(repository, new ListScopesQueryValidator());

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
        var handler = new ListScopesQueryHandler(repository, new ListScopesQueryValidator());

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
        var handler = new ListScopesQueryHandler(repository, new ListScopesQueryValidator());

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
        var handler = new ListScopesQueryHandler(repository, new ListScopesQueryValidator());

        // When
        var output = await handler.HandleAsync(new ListScopesQuery { IncludeDeleted = true, PageNumber = 1, PageSize = 10 });

        // Then
        Assert.Equal(2, output.TotalItems);
    }

    [UnitFact]
    public async Task GivenPageNumberBelowOne_WhenHandlingList_ThenReturnsInvalidPageNumberError()
    {
        // Given — NFR-10: page number must be at least 1
        var repository = await RepositoryWith(NamedScope("Alpha"));
        var handler = new ListScopesQueryHandler(repository, new ListScopesQueryValidator());

        // When
        var output = await handler.HandleAsync(new ListScopesQuery { PageNumber = 0, PageSize = 10 });

        // Then
        Assert.False(output.Success);
        Assert.Contains(PaginationMessages.InvalidPageNumber, output.Errors);
        Assert.Null(output.Data);
    }

    [UnitFact]
    public async Task GivenPageSizeAboveMaximum_WhenHandlingList_ThenReturnsInvalidPageSizeError()
    {
        // Given — NFR-10: page size is bounded so a caller cannot force an unbounded query
        var repository = await RepositoryWith(NamedScope("Alpha"));
        var handler = new ListScopesQueryHandler(repository, new ListScopesQueryValidator());

        // When
        var output = await handler.HandleAsync(new ListScopesQuery { PageNumber = 1, PageSize = 101 });

        // Then
        Assert.False(output.Success);
        Assert.Contains(PaginationMessages.InvalidPageSize, output.Errors);
    }

    [UnitFact]
    public async Task GivenNameFilterLongerThanColumn_WhenHandlingList_ThenReturnsFilterTooLongError()
    {
        // Given — NFR-10: a filter longer than Scope.Name's own 200-char column could never match
        var repository = await RepositoryWith(NamedScope("Alpha"));
        var handler = new ListScopesQueryHandler(repository, new ListScopesQueryValidator());

        // When
        var output = await handler.HandleAsync(
            new ListScopesQuery { Name = new string('a', 201), PageNumber = 1, PageSize = 10 });

        // Then
        Assert.False(output.Success);
        Assert.Contains(PaginationMessages.FilterTooLong, output.Errors);
    }
}
