using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Query.Input;
using ArturRios.Heimdall.Query.Output;
using ArturRios.Mediator.Query.Interfaces;
using ArturRios.Output;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Heimdall.Query.Handlers;

/// <summary>
///     Handles <see cref="ListMyScopeInvitationsQuery" /> (UC-59, FR-SC-16): the caller's own open scope owner
///     invitations, soonest to expire first, for any signed-in Scope Admin or System Admin.
/// </summary>
/// <remarks>
///     Only open invitations, and not those of a logically deleted scope, which accepting would only void.
///     The listing answers 200 with no message, and an empty page when there are none.
/// </remarks>
public class ListMyScopeInvitationsQueryHandler(
    IAsyncReadOnlyRepository<Person, long> personReader,
    IAsyncReadOnlyRepository<ScopeOwnerInvitation, long> invitationReader,
    IValidator<ListMyScopeInvitationsQuery> validator)
    : IPaginatedQueryHandlerAsync<ListMyScopeInvitationsQuery, MyScopeInvitationOutput>
{
    public async Task<PaginatedOutput<MyScopeInvitationOutput>> HandleAsync(
        ListMyScopeInvitationsQuery query, CancellationToken cancellationToken = default)
    {
        var output = PaginatedOutput<MyScopeInvitationOutput>.New;

        // NFR-10: page bounds, before any query runs.
        var validation = await validator.ValidateAsync(query, cancellationToken);

        if (!validation.IsValid)
        {
            return output.WithErrors(validation.Errors.Select(failure => failure.ErrorMessage));
        }

        // The liveness filter has already refused a caller whose record is gone; id 0 matches no row.
        var actor = await personReader.Query()
            .FirstOrDefaultAsync(x => x.PublicId == query.ActingPersonId, cancellationToken);
        var actorId = actor?.Id ?? 0;
        var now = DateTime.UtcNow;

        // Soonest to expire first with the internal id as tiebreaker, ordered on the entity because the id
        // never reaches the output, then projected and paginated over that ordering.
        var projected = invitationReader.Query()
            .Where(x => x.InviteePersonId == actorId && !x.Scope.IsDeleted)
            .Where(ScopeOwnerInvitation.OpenAt(now))
            .OrderBy(x => x.ExpiresAt)
            .ThenBy(x => x.Id)
            .Select(x => new MyScopeInvitationOutput
            {
                Id = x.PublicId,
                ScopeId = x.Scope.PublicId,
                ScopeName = x.Scope.Name,
                InvitedByName = x.InvitedBy == null ? null : x.InvitedBy.Name,
                CreatedAt = x.CreatedAt,
                ExpiresAt = x.ExpiresAt
            });

        return await projected.PaginateAsync(query.PageNumber, query.PageSize, orderBy: null);
    }
}
