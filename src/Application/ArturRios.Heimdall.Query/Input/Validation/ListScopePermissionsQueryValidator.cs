using ArturRios.Heimdall.Shared.Messages;
using FluentValidation;

namespace ArturRios.Heimdall.Query.Input.Validation;

/// <summary>Input validation for <see cref="ListScopePermissionsQuery" /> (UC-32 read b, NFR-10).</summary>
public class ListScopePermissionsQueryValidator : PaginatedQueryValidator<ListScopePermissionsQuery>
{
    public ListScopePermissionsQueryValidator()
    {
        // Matches ScopePermission.Name's own [MaxLength(200)] — a longer filter could never match a row.
        RuleFor(query => query.Name)
            .MaximumLength(200)
            .WithMessage(PaginationMessages.FilterTooLong);
    }
}
