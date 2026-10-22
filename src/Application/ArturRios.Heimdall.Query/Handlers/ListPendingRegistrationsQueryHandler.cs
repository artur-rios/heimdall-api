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
///     Handles <see cref="ListPendingRegistrationsQuery" /> (UC-49, FR-SR-11): a scope's
///     self-registrations awaiting confirmation, newest first, for a System Admin or an owner of the
///     scope.
/// </summary>
/// <remarks>
///     <para>
///         A missing or logically deleted scope is AF-49a (<c>ScopeNotFound</c>), as for the scope's
///         owners listing; a caller who neither is a System Admin nor owns it is AF-49b
///         (<c>NotScopeOwner</c>).
///     </para>
///     <para>
///         Whether the scope is still open to self-registration does not matter. Closing sign-up is
///         when an owner most wants to see, and clear, what was left behind.
///     </para>
///     <para>
///         FR-SR-06's anti-enumeration guarantee is about the anonymous <c>POST /api/auth/register</c>
///         and is untouched: this listing is authenticated and limited to the scope's owners, and a taken
///         address never produces a pending row.
///     </para>
/// </remarks>
public class ListPendingRegistrationsQueryHandler(
    IAsyncReadOnlyRepository<Scope, long> scopeReader,
    IAsyncReadOnlyRepository<PendingRegistration, long> pendingReader,
    IScopeOwnershipChecker scopeOwnership,
    IValidator<ListPendingRegistrationsQuery> validator)
    : IPaginatedQueryHandlerAsync<ListPendingRegistrationsQuery, PendingRegistrationOutput>
{
    public async Task<PaginatedOutput<PendingRegistrationOutput>> HandleAsync(
        ListPendingRegistrationsQuery query, CancellationToken cancellationToken = default)
    {
        var output = PaginatedOutput<PendingRegistrationOutput>.New;

        // NFR-10: page bounds, before any query runs.
        var validation = await validator.ValidateAsync(query, cancellationToken);

        if (!validation.IsValid)
        {
            return output.WithErrors(validation.Errors.Select(failure => failure.ErrorMessage));
        }

        // AF-49a.
        var scope = await scopeReader.Query()
            .FirstOrDefaultAsync(x => x.PublicId == query.ScopeId && !x.IsDeleted, cancellationToken);

        if (scope is null)
        {
            return output.WithError(ScopeMessages.ScopeNotFound);
        }

        // AF-49b: a System Admin bypasses; anyone else must own the scope.
        if (!await scopeOwnership.ActorMayManageScopeAsync(
                query.ActingRole, query.ActingPersonId, scope.Id, cancellationToken))
        {
            return output.WithError(ScopeMessages.NotScopeOwner);
        }

        // One reading of the clock for every row, so "expired" means the same thing across a page.
        var now = DateTime.UtcNow;
        const int budget = PendingRegistration.MaxConfirmationGuesses;

        // Newest first with the internal id as tiebreaker, ordered on the entity because the id never
        // reaches the output, then projected and paginated over that ordering.
        var projected = pendingReader.Query()
            .Where(x => x.ScopeId == scope.Id)
            .OrderByDescending(x => x.CreatedAt)
            .ThenByDescending(x => x.Id)
            .Select(x => new PendingRegistrationOutput
            {
                Id = x.PublicId,
                Name = x.Name,
                Email = x.Email,
                CreatedAt = x.CreatedAt,
                ExpiresAt = x.ExpiresAt,
                // Parallel wrong guesses can push the count past the budget before the row is
                // discarded (see PendingRegistrationConfirmer), so the remainder is floored at zero.
                AttemptsLeft = x.FailedAttempts >= budget ? 0 : budget - x.FailedAttempts,
                Expired = x.ExpiresAt <= now
            });

        var page = await projected.PaginateAsync(query.PageNumber, query.PageSize, orderBy: null, cancellationToken: cancellationToken);

        return page.WithMessage(ScopeMessages.PendingRegistrationsRetrieved);
    }
}
