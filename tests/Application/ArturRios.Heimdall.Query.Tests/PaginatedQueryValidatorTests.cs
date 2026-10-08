using ArturRios.Heimdall.Query.Input;
using ArturRios.Heimdall.Query.Input.Validation;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Util.Test.Attributes;

namespace ArturRios.Heimdall.Query.Tests;

// Unit tests for the page-number bounds every listing shares through PaginatedQueryValidator. The
// upper bound exists because the pagination helper computes the offset in unchecked int arithmetic:
// past it, (PageNumber - 1) * PageSize wraps negative and PostgreSQL refuses the OFFSET with a 500.
public class PaginatedQueryValidatorTests
{
    private static ListScopesQuery Query(int pageNumber, int pageSize = 100) =>
        new() { PageNumber = pageNumber, PageSize = pageSize };

    [UnitFact]
    public async Task GivenTheLastPageWhoseOffsetFits_WhenValidating_ThenItIsAccepted()
    {
        var result = await new ListScopesQueryValidator().ValidateAsync(
            Query(PaginatedQueryValidator<ListScopesQuery>.MaxPageNumber));

        Assert.True(result.IsValid);
    }

    [UnitFact]
    public async Task GivenAPageNumberWhoseOffsetOverflows_WhenValidating_ThenItIsRejected()
    {
        var result = await new ListScopesQueryValidator().ValidateAsync(Query(30_000_000));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, failure => failure.ErrorMessage == PaginationMessages.InvalidPageNumber);
    }

    [UnitFact]
    public void GivenTheBound_WhenTheLargestOffsetIsComputed_ThenItDoesNotOverflow()
    {
        var offset = checked((PaginatedQueryValidator<ListScopesQuery>.MaxPageNumber - 1) * 100);

        Assert.True(offset > 0);
    }
}
