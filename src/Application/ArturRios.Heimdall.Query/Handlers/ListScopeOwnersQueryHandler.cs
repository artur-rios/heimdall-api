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
///     Handles <see cref="ListScopeOwnersQuery" /> (UC-07, FR-PE-04): lists the <c>ScopeAdmin</c>
///     owners of a scope with pagination and optional name/email filters, excluding logically deleted
///     persons unless explicitly requested (FR-PE-08). A missing or logically deleted scope is AF-07a
///     (<c>ScopeNotFound</c>); an actor who does not own the scope is AF-07b (<c>NotScopeOwner</c>).
///     A System Admin bypasses the ownership check.
/// </summary>
public class ListScopeOwnersQueryHandler(
    IAsyncReadOnlyRepository<Scope> scopeReader,
    IAsyncReadOnlyRepository<Person> personReader,
    IScopeOwnershipChecker scopeOwnership,
    IValidator<ListScopeOwnersQuery> validator)
    : IPaginatedQueryHandlerAsync<ListScopeOwnersQuery, PersonOutput>
{
    public async Task<PaginatedOutput<PersonOutput>> HandleAsync(ListScopeOwnersQuery query)
    {
        var output = PaginatedOutput<PersonOutput>.New;

        // NFR-10: page number/size bounds and filter length, validated before any query runs.
        var validation = await validator.ValidateAsync(query);

        if (!validation.IsValid)
        {
            return output.WithErrors(validation.Errors.Select(failure => failure.ErrorMessage));
        }

        // AF-07a: the target scope must exist and not be logically deleted.
        var scope = await scopeReader.Query()
            .FirstOrDefaultAsync(x => x.PublicId == query.ScopeId && !x.IsDeleted);

        if (scope is null)
        {
            return output.WithError(PersonMessages.ScopeNotFound);
        }

        // AF-07b: a Scope Admin may only read a scope they own; a System Admin bypasses.
        if (!await scopeOwnership.ActorMayManageScopeAsync(query.ActingRole, query.ActingPersonId, scope.Id))
        {
            return output.WithError(PersonMessages.NotScopeOwner);
        }

        var owners = personReader.Query()
            .Where(x => x.ScopeOwnerships.Any(ownership => ownership.ScopeId == scope.Id));

        if (!query.IncludeDeleted)
        {
            owners = owners.Where(x => !x.IsDeleted);
        }

        if (!string.IsNullOrWhiteSpace(query.Name))
        {
            var name = query.Name.ToLower();
            owners = owners.Where(x => x.Name.ToLower().Contains(name));
        }

        if (!string.IsNullOrWhiteSpace(query.Email))
        {
            var email = query.Email.ToLower();
            owners = owners.Where(x => x.Email.ToLower().Contains(email));
        }

        var projected = owners.Select(x => new PersonOutput
        {
            Id = x.PublicId,
            Name = x.Name,
            Email = x.Email,
            Role = (int)x.RoleId,
            EmailVerified = x.EmailVerified,
            IsDeleted = x.IsDeleted,
            ScopeId = x.ScopeMembership != null ? x.ScopeMembership.Scope.PublicId : null,
            OwnedScopeIds = x.ScopeOwnerships.Select(ownership => ownership.Scope.PublicId).ToList(),
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

        return page.WithMessage(PersonMessages.PersonsRetrievedSuccessfully);
    }
}
