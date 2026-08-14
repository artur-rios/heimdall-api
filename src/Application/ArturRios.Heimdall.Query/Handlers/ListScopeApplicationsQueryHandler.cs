using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Query.Input;
using ArturRios.Heimdall.Query.Output;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Heimdall.Shared.Services;
using ArturRios.Mediator.Query.Interfaces;
using ArturRios.Output;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Application = ArturRios.Heimdall.Domain.Entities.Application;

namespace ArturRios.Heimdall.Query.Handlers;

/// <summary>
///     Handles <see cref="ListScopeApplicationsQuery" /> (UC-17, FR-AP-05): lists a scope's
///     applications with pagination and optional name/owner filters, excluding logically deleted
///     applications unless explicitly requested (FR-AP-09). A System Admin sees every application in
///     the scope; any other actor must own the scope and sees only the applications they own. A
///     missing or logically deleted scope is AF-17a (<c>ScopeNotFound</c>); an actor who does not own
///     the scope is AF-17b (<c>NotScopeOwner</c>).
/// </summary>
public class ListScopeApplicationsQueryHandler(
    IAsyncReadOnlyRepository<Scope> scopeReader,
    IAsyncReadOnlyRepository<Application> applicationReader,
    IScopeOwnershipChecker scopeOwnership,
    IValidator<ListScopeApplicationsQuery> validator)
    : IPaginatedQueryHandlerAsync<ListScopeApplicationsQuery, ApplicationOutput>
{
    public async Task<PaginatedOutput<ApplicationOutput>> HandleAsync(ListScopeApplicationsQuery query)
    {
        var output = PaginatedOutput<ApplicationOutput>.New;

        // NFR-10: page number/size bounds and filter length, validated before any query runs.
        var validation = await validator.ValidateAsync(query);

        if (!validation.IsValid)
        {
            return output.WithErrors(validation.Errors.Select(failure => failure.ErrorMessage));
        }

        // AF-17a: the target scope must exist and not be logically deleted.
        var scope = await scopeReader.Query()
            .FirstOrDefaultAsync(x => x.PublicId == query.ScopeId && !x.IsDeleted);

        if (scope is null)
        {
            return output.WithError(ApplicationMessages.ScopeNotFound);
        }

        // AF-17b: a Scope Admin may only list a scope they own; a System Admin bypasses. The gate is
        // not redundant with the owner narrowing below: without it, an actor probing an unrelated
        // scope would get an empty 200, indistinguishable from a scope that is genuinely empty.
        if (!await scopeOwnership.ActorMayManageScopeAsync(query.ActingRole, query.ActingPersonId, scope.Id))
        {
            return output.WithError(ApplicationMessages.NotScopeOwner);
        }

        var applications = applicationReader.Query().Where(x => x.ScopeId == scope.Id);

        // UC-17 step 2: a System Admin sees every application in the scope; a Scope Admin sees only
        // the ones they own, even among co-owners of the same scope.
        if (query.ActingRole != (int)Roles.SystemAdmin)
        {
            applications = applications.Where(x => x.Owner.PublicId == query.ActingPersonId);
        }

        // FR-AP-09: logically deleted applications are excluded unless explicitly requested.
        if (!query.IncludeDeleted)
        {
            applications = applications.Where(x => !x.IsDeleted);
        }

        // Compared case-insensitively (LOWER() in SQL), as every name filter in this codebase is.
        if (!string.IsNullOrWhiteSpace(query.Name))
        {
            var name = query.Name.ToLower();
            applications = applications.Where(x => x.Name.ToLower().Contains(name));
        }

        if (query.OwnerId.HasValue)
        {
            applications = applications.Where(x => x.Owner.PublicId == query.OwnerId.Value);
        }

        var projected = applications.Select(x => new ApplicationOutput
        {
            Id = x.PublicId,
            Name = x.Name,
            ScopeId = x.Scope.PublicId,
            OwnerId = x.Owner.PublicId,
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

        return page.WithMessage(ApplicationMessages.ApplicationsRetrievedSuccessfully);
    }
}
