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

namespace ArturRios.Heimdall.Query.Handlers;

/// <summary>
///     Handles <see cref="ListScopeAdminsQuery" /> (UC-07 read d, FR-PE-12): lists every live
///     <c>ScopeAdmin</c> with pagination and optional name/email filters, projected to identifier,
///     name, and email only. Backs UI-11's owner selector and UI-14's "add an existing Scope Admin".
/// </summary>
/// <remarks>
///     <para>
///         Both administrator roles may call it, which is wider than UC-07's per-person visibility
///         rule allows for a Scope Admin. That widening is deliberate and is what makes UI-14 step 3
///         possible at all — a co-owner being added does not yet own the scope, so the existing rule
///         could never surface them. The three-field projection is what keeps it safe; see
///         <see cref="PersonSummaryOutput" />.
///     </para>
///     <para>
///         <c>ExcludeOwnersOfScopeId</c> is gated on scope ownership even though the projection is
///         minimal, because the parameter is not a projection question: calling the endpoint twice,
///         once with it and once without, and diffing the results enumerates the owners of whatever
///         scope was named. The gate makes that possible only for a scope the caller already manages.
///     </para>
/// </remarks>
public class ListScopeAdminsQueryHandler(
    IAsyncReadOnlyRepository<Scope> scopeReader,
    IAsyncReadOnlyRepository<Person> personReader,
    IScopeOwnershipChecker scopeOwnership,
    IValidator<ListScopeAdminsQuery> validator)
    : IPaginatedQueryHandlerAsync<ListScopeAdminsQuery, PersonSummaryOutput>
{
    public async Task<PaginatedOutput<PersonSummaryOutput>> HandleAsync(ListScopeAdminsQuery query)
    {
        var output = PaginatedOutput<PersonSummaryOutput>.New;

        // NFR-10: page number/size bounds and filter length, validated before any query runs.
        var validation = await validator.ValidateAsync(query);

        if (!validation.IsValid)
        {
            return output.WithErrors(validation.Errors.Select(failure => failure.ErrorMessage));
        }

        long? excludedScopeId = null;

        if (query.ExcludeOwnersOfScopeId is not null)
        {
            // AF-07a: the named scope must exist and not be logically deleted.
            var scope = await scopeReader.Query()
                .FirstOrDefaultAsync(x => x.PublicId == query.ExcludeOwnersOfScopeId.Value && !x.IsDeleted);

            if (scope is null)
            {
                return output.WithError(PersonMessages.ScopeNotFound);
            }

            // AF-07b: only an owner of the named scope (or a System Admin) may subtract its owners,
            // since a with/without diff would otherwise reveal them.
            if (!await scopeOwnership.ActorMayManageScopeAsync(query.ActingRole, query.ActingPersonId, scope.Id))
            {
                return output.WithError(PersonMessages.NotScopeOwner);
            }

            excludedScopeId = scope.Id;
        }

        // A logically deleted administrator is never a valid owner, so this listing has no
        // include-deleted mode at all — see ListScopeAdminsQuery.
        var admins = personReader.Query()
            .Where(x => x.RoleId == (long)Roles.ScopeAdmin && !x.IsDeleted);

        if (excludedScopeId is not null)
        {
            // Before pagination, so a page of the requested size comes back full (UI-14 AF-14c).
            admins = admins.Where(x => x.ScopeOwnerships.All(ownership => ownership.ScopeId != excludedScopeId.Value));
        }

        if (!string.IsNullOrWhiteSpace(query.Name))
        {
            var name = query.Name.ToLower();
            admins = admins.Where(x => x.Name.ToLower().Contains(name));
        }

        if (!string.IsNullOrWhiteSpace(query.Email))
        {
            var email = query.Email.ToLower();
            admins = admins.Where(x => x.Email.ToLower().Contains(email));
        }

        var projected = admins.Select(x => new PersonSummaryOutput
        {
            Id = x.PublicId,
            Name = x.Name,
            Email = x.Email
        });

        // Ordered by name with the public identifier as a tiebreaker, then paginated over that
        // ordering — the same reasoning ListScopePersonsQueryHandler documents: names are not
        // unique, PostgreSQL gives no ordering guarantee between tied sort keys, and each page is a
        // separate query, so without the tiebreaker two administrators sharing a name could straddle
        // a page boundary and appear on both pages while a third appeared on neither.
        var ordered = projected.OrderBy(x => x.Name).ThenBy(x => x.Id);

        var page = await ordered.PaginateAsync(query.PageNumber, query.PageSize, orderBy: null);

        return page.WithMessage(PersonMessages.PersonsRetrievedSuccessfully);
    }
}
