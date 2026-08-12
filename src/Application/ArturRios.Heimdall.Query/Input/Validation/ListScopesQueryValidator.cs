using ArturRios.Heimdall.Shared.Messages;
using FluentValidation;

namespace ArturRios.Heimdall.Query.Input.Validation;

/// <summary>Input validation for <see cref="ListScopesQuery" /> (UC-02, NFR-10).</summary>
public class ListScopesQueryValidator : PaginatedQueryValidator<ListScopesQuery>
{
    public ListScopesQueryValidator()
    {
        // Matches Scope.Name's own [MaxLength(200)] — a longer filter could never match a row.
        RuleFor(query => query.Name)
            .MaximumLength(200)
            .WithMessage(PaginationMessages.FilterTooLong);
    }
}
