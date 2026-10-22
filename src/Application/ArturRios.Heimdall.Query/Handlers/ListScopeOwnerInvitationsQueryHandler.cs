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
///     Handles <see cref="ListScopeOwnerInvitationsQuery" /> (UC-58, FR-SC-15): a scope's open owner
///     invitations, newest first, for a System Admin or an owner of the scope.
/// </summary>
/// <remarks>
///     A missing or logically deleted scope is AF-58a; a caller who may not manage it is AF-58b. Only open
///     invitations are listed — not ended, not expired — and not those of a logically deleted invitee, who
///     could never accept, nor of a processing-restricted one (NFR-24). The inviter's name is shown only for
///     a live, unrestricted inviter. The listing answers 200 with no message.
/// </remarks>
public class ListScopeOwnerInvitationsQueryHandler(
    IAsyncReadOnlyRepository<Scope, long> scopeReader,
    IAsyncReadOnlyRepository<ScopeOwnerInvitation, long> invitationReader,
    IScopeOwnershipChecker scopeOwnership,
    IValidator<ListScopeOwnerInvitationsQuery> validator)
    : IPaginatedQueryHandlerAsync<ListScopeOwnerInvitationsQuery, ScopeOwnerInvitationOutput>
{
    public async Task<PaginatedOutput<ScopeOwnerInvitationOutput>> HandleAsync(
        ListScopeOwnerInvitationsQuery query, CancellationToken cancellationToken = default)
    {
        var output = PaginatedOutput<ScopeOwnerInvitationOutput>.New;

        // NFR-10: page bounds, before any query runs.
        var validation = await validator.ValidateAsync(query, cancellationToken);

        if (!validation.IsValid)
        {
            return output.WithErrors(validation.Errors.Select(failure => failure.ErrorMessage));
        }

        // AF-58a.
        var scope = await scopeReader.Query()
            .FirstOrDefaultAsync(x => x.PublicId == query.ScopeId && !x.IsDeleted, cancellationToken);

        if (scope is null)
        {
            return output.WithError(ScopeMessages.ScopeNotFound);
        }

        // AF-58b.
        if (!await scopeOwnership.ActorMayManageScopeAsync(
                query.ActingRole, query.ActingPersonId, scope.Id, cancellationToken))
        {
            return output.WithError(ScopeMessages.NotScopeOwner);
        }

        var now = DateTime.UtcNow;

        // Newest first with the internal id as tiebreaker, ordered on the entity because the id never
        // reaches the output, then projected and paginated over that ordering.
        // NFR-24: a restricted identity is withheld from tenant-facing listings — the invitee's row and the
        // inviter's name alike, as ListScopeOwners does.
        var projected = invitationReader.Query()
            .Where(x => x.ScopeId == scope.Id && !x.Invitee.IsDeleted && x.Invitee.ProcessingRestrictedAt == null)
            .Where(ScopeOwnerInvitation.OpenAt(now))
            .OrderByDescending(x => x.CreatedAt)
            .ThenByDescending(x => x.Id)
            .Select(x => new ScopeOwnerInvitationOutput
            {
                Id = x.PublicId,
                InviteeId = x.Invitee.PublicId,
                InviteeName = x.Invitee.Name,
                InviteeEmail = x.Invitee.Email,
                InvitedByName =
                    x.InvitedBy != null && !x.InvitedBy.IsDeleted && x.InvitedBy.ProcessingRestrictedAt == null
                        ? x.InvitedBy.Name
                        : null,
                CreatedAt = x.CreatedAt,
                ExpiresAt = x.ExpiresAt
            });

        return await projected.PaginateAsync(query.PageNumber, query.PageSize, orderBy: null, cancellationToken: cancellationToken);
    }
}
