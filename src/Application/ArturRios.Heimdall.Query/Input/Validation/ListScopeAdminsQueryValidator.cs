using ArturRios.Heimdall.Shared.Messages;
using FluentValidation;

namespace ArturRios.Heimdall.Query.Input.Validation;

/// <summary>Input validation for <see cref="ListScopeAdminsQuery" /> (UC-07 read d, NFR-10).</summary>
public class ListScopeAdminsQueryValidator : PaginatedQueryValidator<ListScopeAdminsQuery>
{
    public ListScopeAdminsQueryValidator()
    {
        // Matches Person.Name/Email's own [MaxLength] — a longer filter could never match a row.
        RuleFor(query => query.Name)
            .MaximumLength(200)
            .WithMessage(PaginationMessages.FilterTooLong);

        RuleFor(query => query.Email)
            .MaximumLength(256)
            .WithMessage(PaginationMessages.FilterTooLong);
    }
}
