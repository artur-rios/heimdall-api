using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Heimdall.Shared.Time;
using FluentValidation;

namespace ArturRios.Heimdall.Query.Input.Validation;

/// <summary>Input validation for <see cref="ListAuditLogQuery" /> (UC-48 AF-48a, NFR-10).</summary>
public class ListAuditLogQueryValidator : PaginatedQueryValidator<ListAuditLogQuery>
{
    /// <summary>Matches <c>AuditLog.Action</c>'s <c>[MaxLength]</c>: a longer filter could never match a row.</summary>
    private const int MaxActionLength = 200;

    public ListAuditLogQueryValidator()
    {
        // Compared as UTC on both sides, so an offset-less bound and a "Z" bound compare as the
        // handler will filter with them.
        RuleFor(query => query.To)
            .Must((query, to) =>
                to is null || query.From is null || UtcInstant.Of(to.Value) >= UtcInstant.Of(query.From.Value))
            .WithMessage(AuditMessages.InvalidDateRange);

        RuleFor(query => query.Action)
            .MaximumLength(MaxActionLength)
            .WithMessage(PaginationMessages.FilterTooLong);
    }
}
