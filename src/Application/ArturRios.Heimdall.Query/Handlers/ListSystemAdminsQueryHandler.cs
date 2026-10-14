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
///     Handles <see cref="ListSystemAdminsQuery" /> (UC-07 read e, FR-PE-13): lists every live
///     <c>SystemAdmin</c> with pagination and optional name/email filters, projected to identifier,
///     name, and email only — who else holds full access to the deployment.
/// </summary>
/// <remarks>
///     Authorization is entirely the endpoint's, as in <see cref="ListErasureRequestsQueryHandler" />:
///     the only actor is the System Admin, and the <c>RoleRequirement</c> settles it, so there is no
///     data-dependent rule left here. A Scope Admin is refused because a System Admin's address is
///     not tenant-facing — the same reason <see cref="ListScopeAdminsQueryHandler" /> leaves System
///     Admins out of a Scope Admin's owner picker.
/// </remarks>
public class ListSystemAdminsQueryHandler(
    IAsyncReadOnlyRepository<Person, long> personReader,
    IValidator<ListSystemAdminsQuery> validator)
    : IPaginatedQueryHandlerAsync<ListSystemAdminsQuery, PersonSummaryOutput>
{
    public async Task<PaginatedOutput<PersonSummaryOutput>> HandleAsync(ListSystemAdminsQuery query, CancellationToken cancellationToken = default)
    {
        var output = PaginatedOutput<PersonSummaryOutput>.New;

        // NFR-10: page number/size bounds and filter length, validated before any query runs.
        var validation = await validator.ValidateAsync(query);

        if (!validation.IsValid)
        {
            return output.WithErrors(validation.Errors.Select(failure => failure.ErrorMessage));
        }

        // A logically deleted administrator holds no access, so this listing has no include-deleted
        // mode at all — see ListSystemAdminsQuery.
        var admins = personReader.Query()
            .Where(x => x.RoleId == (long)Roles.SystemAdmin && !x.IsDeleted
                        // NFR-24: withheld from listings, as in ListScopeAdmins. A System Admin can
                        // still reach a restricted one by identifier (UC-07 read a).
                        && x.ProcessingRestrictedAt == null);

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
