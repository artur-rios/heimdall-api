using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Query.Input;
using ArturRios.Heimdall.Query.Output;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Mediator.Query.Interfaces;
using ArturRios.Output;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Heimdall.Query.Handlers;

/// <summary>
///     Handles <see cref="ListErasureRequestsQuery" /> (UC-43): lists the erasure requests that have
///     been made and not yet carried out, across both identity tables, soonest deadline first.
/// </summary>
/// <remarks>
///     <para>
///         Authorization is entirely the endpoint's: UC-43's only actor is the System Admin, and the
///         <c>RoleRequirement</c> settles it, so there is no data-dependent rule left here. That is
///         deliberate rather than an omission — a Scope Admin seeing the erasure requests of the
///         users in their scope would learn which of them had asked to leave, which is not something
///         the request itself entitles them to know.
///     </para>
///     <para>
///         The two tables are read separately and combined in memory rather than joined. They have
///         no relationship — the whole design keeps them apart (TH-21) — and the result set is
///         bounded by what is outstanding at one moment, which is a queue of things needing human
///         attention rather than a table scan.
///     </para>
/// </remarks>
public class ListErasureRequestsQueryHandler(
    IAsyncReadOnlyRepository<Person> personReader,
    IAsyncReadOnlyRepository<GoogleUser> googleUserReader,
    IValidator<ListErasureRequestsQuery> validator)
    : IPaginatedQueryHandlerAsync<ListErasureRequestsQuery, ErasureRequestOutput>
{
    public async Task<PaginatedOutput<ErasureRequestOutput>> HandleAsync(ListErasureRequestsQuery query)
    {
        var output = PaginatedOutput<ErasureRequestOutput>.New;

        // NFR-10: page number and size bounds, validated before any query runs.
        var validation = await validator.ValidateAsync(query);

        if (!validation.IsValid)
        {
            return output.WithErrors(validation.Errors.Select(failure => failure.ErrorMessage));
        }

        var now = DateTime.UtcNow;

        // Outstanding means asked for and not yet carried out. A record already anonymised is done,
        // whatever else is true of it.
        var persons = await personReader.Query()
            .Where(person => person.ErasureRequestedAt != null && person.AnonymisedAt == null)
            .Select(person => new ErasureRequestOutput
            {
                SubjectId = person.PublicId,
                IsGoogleUser = false,
                RequestedAt = person.ErasureRequestedAt!.Value,
                DueAt = person.ErasureDueAt!.Value,
                BlockedReason = person.ErasureBlockedReason
            })
            .ToListAsync();

        var googleUsers = await googleUserReader.Query()
            .Where(googleUser => googleUser.ErasureRequestedAt != null && googleUser.AnonymisedAt == null)
            .Select(googleUser => new ErasureRequestOutput
            {
                SubjectId = googleUser.PublicId,
                IsGoogleUser = true,
                RequestedAt = googleUser.ErasureRequestedAt!.Value,
                DueAt = googleUser.ErasureDueAt!.Value,
                BlockedReason = googleUser.ErasureBlockedReason
            })
            .ToListAsync();

        var requests = persons.Concat(googleUsers).ToList();

        // Computed here rather than in the projection: "overdue" is a comparison against the moment
        // the queue is read, not a stored fact, and computing it in SQL would have each of the two
        // queries take its own reading of the clock.
        foreach (var request in requests)
        {
            request.Overdue = request.DueAt <= now;
        }

        if (query.OverdueOnly)
        {
            requests = requests.Where(request => request.Overdue).ToList();
        }

        // Soonest deadline first, with the subject's identifier as a tiebreaker so two requests
        // sharing a deadline cannot straddle a page boundary — the same reasoning the person
        // listings document.
        var ordered = requests
            .OrderBy(request => request.DueAt)
            .ThenBy(request => request.SubjectId)
            .AsQueryable();

        var page = await Task.FromResult(
            ordered.Paginate(query.PageNumber, query.PageSize, orderBy: null));

        return page.WithMessage(ErasureMessages.ErasureRequestsRetrieved);
    }
}
