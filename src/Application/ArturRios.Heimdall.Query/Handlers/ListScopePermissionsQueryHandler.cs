using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Query.Input;
using ArturRios.Heimdall.Query.Output;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Heimdall.Shared.Services;
using ArturRios.Mediator.Query.Interfaces;
using ArturRios.Output;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Heimdall.Query.Handlers;

/// <summary>
///     Handles <see cref="ListScopePermissionsQuery" /> (UC-32, FR-SP-05): lists a scope's
///     permissions with pagination and an optional name filter, excluding logically deleted
///     permissions unless explicitly requested (FR-SP-09). A System Admin sees every permission in
///     the scope; any other actor must own the scope. A missing or logically deleted scope reuses
///     AF-31a (<c>ScopeNotFound</c>); an actor who does not own the scope is AF-32e
///     (<c>NotScopeOwner</c>). A scope permission has no owner of its own, so unlike applications
///     there is no per-owner narrowing — owning the scope is the whole of the rule.
/// </summary>
public class ListScopePermissionsQueryHandler(
    IAsyncReadOnlyRepository<Scope> scopeReader,
    IAsyncReadOnlyRepository<ScopePermission> permissionReader,
    IScopeOwnershipChecker scopeOwnership,
    IValidator<ListScopePermissionsQuery> validator)
    : IPaginatedQueryHandlerAsync<ListScopePermissionsQuery, ScopePermissionOutput>
{
    public async Task<PaginatedOutput<ScopePermissionOutput>> HandleAsync(ListScopePermissionsQuery query)
    {
        var output = PaginatedOutput<ScopePermissionOutput>.New;

        // NFR-10: page number/size bounds and filter length, validated before any query runs.
        var validation = await validator.ValidateAsync(query);

        if (!validation.IsValid)
        {
            return output.WithErrors(validation.Errors.Select(failure => failure.ErrorMessage));
        }

        // AF-31a (reused): the target scope must exist and not be logically deleted.
        var scope = await scopeReader.Query()
            .FirstOrDefaultAsync(x => x.PublicId == query.ScopeId && !x.IsDeleted);

        if (scope is null)
        {
            return output.WithError(ScopePermissionMessages.ScopeNotFound);
        }

        // AF-32e: a Scope Admin may only list a scope they own; a System Admin bypasses inside the
        // checker. The gate is not redundant with the empty-page outcome: without it, an actor
        // probing an unrelated scope would get an empty 200, indistinguishable from a scope that is
        // genuinely empty.
        if (!await scopeOwnership.ActorMayManageScopeAsync(query.ActingRole, query.ActingPersonId, scope.Id))
        {
            return output.WithError(ScopePermissionMessages.NotScopeOwner);
        }

        var permissions = permissionReader.Query().Where(x => x.ScopeId == scope.Id);

        // FR-SP-09: logically deleted permissions are excluded unless explicitly requested.
        if (!query.IncludeDeleted)
        {
            permissions = permissions.Where(x => !x.IsDeleted);
        }

        // Compared case-insensitively (LOWER() in SQL), as every name filter in this codebase is.
        if (!string.IsNullOrWhiteSpace(query.Name))
        {
            var name = query.Name.ToLower();
            permissions = permissions.Where(x => x.Name.ToLower().Contains(name));
        }

        var projected = permissions.Select(x => new ScopePermissionOutput
        {
            Id = x.PublicId,
            Name = x.Name,
            Description = x.Description,
            IncludeAsJwtClaim = x.IncludeAsJwtClaim,
            ScopeId = x.Scope.PublicId,
            IsDeleted = x.IsDeleted,
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

        var page = await ordered.PaginateAsync(query.PageNumber, query.PageSize, orderBy: null);

        return page.WithMessage(ScopePermissionMessages.ScopePermissionsRetrievedSuccessfully);
    }
}
