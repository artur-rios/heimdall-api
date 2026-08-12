using ArturRios.Heimdall.Shared.Messages;
using FluentValidation;

namespace ArturRios.Heimdall.Query.Input.Validation;

/// <summary>Input validation for <see cref="ListScopeGoogleUsersQuery" /> (UC-27, NFR-10).</summary>
public class ListScopeGoogleUsersQueryValidator : PaginatedQueryValidator<ListScopeGoogleUsersQuery>
{
    public ListScopeGoogleUsersQueryValidator()
    {
        // Matches GoogleUser.Name/Email's own [MaxLength] — a longer filter could never match a row.
        RuleFor(query => query.Name)
            .MaximumLength(200)
            .WithMessage(PaginationMessages.FilterTooLong);

        RuleFor(query => query.Email)
            .MaximumLength(256)
            .WithMessage(PaginationMessages.FilterTooLong);
    }
}
