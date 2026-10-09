using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Mediator.Query;
using FluentValidation;

namespace ArturRios.Heimdall.Query.Input.Validation;

/// <summary>
///     Shared pagination rules for every paginated list query (NFR-10): <c>PageNumber</c> must be at
///     least 1, and <c>PageSize</c> must fall within <see cref="MaxPageSize" />. Concrete query
///     validators derive from this and add their own filter-length rules on top — FluentValidation
///     accumulates rules added by both the base and derived constructors into the same rule set.
/// </summary>
public abstract class PaginatedQueryValidator<TQuery> : AbstractValidator<TQuery> where TQuery : BaseQuery
{
    /// <summary>
    ///     The upper bound on <c>PageSize</c>. Matches <see cref="BaseQuery" />'s own default of 100,
    ///     so a caller who never sets <c>PageSize</c> is always within bounds.
    /// </summary>
    protected const int MaxPageSize = 100;

    /// <summary>
    ///     The last page number whose offset, <c>(PageNumber - 1) * PageSize</c>, still fits in an
    ///     <see cref="int" /> at the largest page size. The pagination helper computes that offset in
    ///     unchecked arithmetic, so a larger number wrapped to a negative OFFSET, which PostgreSQL
    ///     rejects — a 500 from nothing more than an absurd query string.
    /// </summary>
    public const int MaxPageNumber = int.MaxValue / MaxPageSize;

    protected PaginatedQueryValidator()
    {
        RuleFor(query => query.PageNumber)
            .InclusiveBetween(1, MaxPageNumber)
            .WithMessage(PaginationMessages.InvalidPageNumber);

        RuleFor(query => query.PageSize)
            .InclusiveBetween(1, MaxPageSize)
            .WithMessage(PaginationMessages.InvalidPageSize);
    }
}
