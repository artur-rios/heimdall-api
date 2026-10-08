using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Query.Input;
using ArturRios.Heimdall.Query.Output;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Mediator.Query.Interfaces;
using ArturRios.Output;
using FluentValidation;

namespace ArturRios.Heimdall.Query.Handlers;

/// <summary>
///     Handles <see cref="ListScopesQuery" /> (UC-02 read b, FR-SC-03): lists scopes with pagination
///     and an optional name filter, excluding logically deleted scopes unless explicitly requested
///     (FR-SC-07). A System Admin lists every scope; a Scope Admin only the scopes they own.
/// </summary>
/// <remarks>
///     Ownership is read from the <c>SCOPE_OWNER</c> rows, never from the token's owned-scope claim:
///     that claim is a snapshot from sign-in, and an owner removed since (UC-22) must stop seeing the
///     scope at once rather than when their token expires. The filter runs before pagination, so the
///     total is the caller's own count — the number of other tenants' scopes stays out of reach, which
///     is why the listing used to be System-Admin-only.
/// </remarks>
public class ListScopesQueryHandler(
    IAsyncReadOnlyRepository<Scope, long> scopeReader,
    IValidator<ListScopesQuery> validator)
    : IPaginatedQueryHandlerAsync<ListScopesQuery, ScopeOutput>
{
    public async Task<PaginatedOutput<ScopeOutput>> HandleAsync(ListScopesQuery query, CancellationToken cancellationToken = default)
    {
        // NFR-10: page number/size bounds and filter length, validated before any query runs.
        var validation = await validator.ValidateAsync(query);

        if (!validation.IsValid)
        {
            return PaginatedOutput<ScopeOutput>.New
                .WithErrors(validation.Errors.Select(failure => failure.ErrorMessage));
        }

        var scopes = scopeReader.Query();

        // AF-02b (read b). Any other role is refused here as well as at the controller, so a role
        // added later cannot inherit the whole collection by default.
        switch (query.ActingRole)
        {
            case (int)Roles.SystemAdmin:
                break;
            case (int)Roles.ScopeAdmin:
                scopes = scopes.Where(x => x.Owners.Any(owner => owner.Person.PublicId == query.ActingPersonId));
                break;
            default:
                return PaginatedOutput<ScopeOutput>.New.WithError(ScopeMessages.NotAuthorizedToListScopes);
        }

        if (!query.IncludeDeleted)
        {
            scopes = scopes.Where(x => !x.IsDeleted);
        }

        // Compared case-insensitively (LOWER() in SQL), as the person listings do (FR-PE-04) and as
        // every name/email comparison in this codebase does.
        if (!string.IsNullOrWhiteSpace(query.Name))
        {
            var name = query.Name.ToLower();
            scopes = scopes.Where(x => x.Name.ToLower().Contains(name));
        }

        var projected = scopes.Select(x => new ScopeOutput
        {
            Id = x.PublicId,
            Name = x.Name,
            Description = x.Description,
            GoogleSignInEnabled = x.GoogleSignInEnabled,
            DefaultLegalBasis = x.DefaultLegalBasis,
            PrivacyNoticeUri = x.PrivacyNoticeUri,
            IsDeleted = x.IsDeleted,
            OwnerIds = x.Owners.Select(owner => owner.Person.PublicId).ToList(),
            CreatedAt = x.CreatedAt,
            UpdatedAt = x.UpdatedAt
        });

        // Ordered by name with the public identifier as a tiebreaker, then paginated over that
        // ordering rather than handing PaginateAsync a sort key of its own.
        //
        // Name is not unique, and PostgreSQL gives no ordering guarantee between rows whose sort key
        // ties — each page is a separate query, free to break the tie differently. Two people called
        // "Ana Silva" straddling a page boundary could therefore appear on both pages while somebody
        // else appeared on neither. The tiebreaker makes the total order deterministic, so paging
        // through a list sees every row exactly once.
        var ordered = projected.OrderBy(x => x.Name).ThenBy(x => x.Id);

        var output = await ordered.PaginateAsync(query.PageNumber, query.PageSize, orderBy: null);

        return output.WithMessage(ScopeMessages.ScopesRetrievedSuccessfully);
    }
}
