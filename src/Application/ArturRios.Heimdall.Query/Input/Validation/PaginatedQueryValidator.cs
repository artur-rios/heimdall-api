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

    protected PaginatedQueryValidator()
    {
        RuleFor(query => query.PageNumber)
            .GreaterThanOrEqualTo(1)
            .WithMessage(PaginationMessages.InvalidPageNumber);

        RuleFor(query => query.PageSize)
            .InclusiveBetween(1, MaxPageSize)
            .WithMessage(PaginationMessages.InvalidPageSize);
    }
}
