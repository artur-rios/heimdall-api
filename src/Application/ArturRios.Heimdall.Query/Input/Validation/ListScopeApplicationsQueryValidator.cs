using ArturRios.Heimdall.Shared.Messages;
using FluentValidation;

namespace ArturRios.Heimdall.Query.Input.Validation;

/// <summary>Input validation for <see cref="ListScopeApplicationsQuery" /> (UC-17, NFR-10).</summary>
public class ListScopeApplicationsQueryValidator : PaginatedQueryValidator<ListScopeApplicationsQuery>
{
    public ListScopeApplicationsQueryValidator()
    {
        // Matches Application.Name's own [MaxLength(200)] — a longer filter could never match a row.
        RuleFor(query => query.Name)
            .MaximumLength(200)
            .WithMessage(PaginationMessages.FilterTooLong);
    }
}
