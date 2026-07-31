using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.IdentityManager.Domain.Entities;
using ArturRios.IdentityManager.Domain.Enums;
using ArturRios.IdentityManager.Query.Input;
using ArturRios.IdentityManager.Query.Output;
using ArturRios.IdentityManager.Shared.Messages;
using ArturRios.IdentityManager.Shared.Services;
using ArturRios.Mediator.Query.Interfaces;
using ArturRios.Output;
using Microsoft.EntityFrameworkCore;
using Application = ArturRios.IdentityManager.Domain.Entities.Application;

namespace ArturRios.IdentityManager.Query.Handlers;

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
    IScopeOwnershipChecker scopeOwnership)
    : IPaginatedQueryHandlerAsync<ListScopeApplicationsQuery, ApplicationOutput>
{
    public async Task<PaginatedOutput<ApplicationOutput>> HandleAsync(ListScopeApplicationsQuery query)
    {
        var output = PaginatedOutput<ApplicationOutput>.New;

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

        var page = await projected.PaginateAsync(query.PageNumber, query.PageSize, x => x.Name);

        return page.WithMessage(ApplicationMessages.ApplicationsRetrievedSuccessfully);
    }
}
