using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Data.Relational.Core.Repositories;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Heimdall.Shared.Services;
using ArturRios.Mediator.Command.Interfaces;
using ArturRios.Output;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Heimdall.Command.Handlers;

/// <summary>
///     Handles <see cref="DiscardPendingRegistrationCommand" /> (UC-50, FR-SR-12): removes a scope's
///     pending self-registration, so its mailed link stops working and confirming it behaves as for any
///     unknown token (UC-14 AF-14c). The registrant may simply sign up again.
/// </summary>
/// <remarks>
///     <para>
///         Same access rule as UC-49: a System Admin, or an owner of the scope. A missing or logically
///         deleted scope is AF-50a; a caller who may not manage it is AF-50b; a registration that is not
///         this scope's is AF-50c, indistinguishable from one that does not exist.
///     </para>
///     <para>
///         A delete that loses a race — the registrant confirmed, or the purge removed the row, after
///         the lookup — is reported as not found. The row is gone either way, and a conflict would ask
///         the owner to retry something there is nothing left to do.
///     </para>
/// </remarks>
public class DiscardPendingRegistrationCommandHandler(
    IAsyncReadOnlyRepository<Scope, long> scopeReader,
    IAsyncReadOnlyRepository<PendingRegistration, long> pendingReader,
    IAsyncRepository<PendingRegistration, long> pendingWriter,
    IScopeOwnershipChecker scopeOwnership)
    : ICommandHandlerAsync<DiscardPendingRegistrationCommand, DiscardPendingRegistrationCommandOutput>
{
    public async Task<DataOutput<DiscardPendingRegistrationCommandOutput?>> HandleAsync(
        DiscardPendingRegistrationCommand command, CancellationToken cancellationToken = default)
    {
        var output = DataOutput<DiscardPendingRegistrationCommandOutput?>.New;

        // AF-50a.
        var scope = await scopeReader.Query()
            .FirstOrDefaultAsync(x => x.PublicId == command.ScopeId && !x.IsDeleted, cancellationToken);

        if (scope is null)
        {
            return output.WithError(ScopeMessages.ScopeNotFound);
        }

        // AF-50b. Before the registration lookup, so a non-owner cannot learn which ids exist.
        if (!await scopeOwnership.ActorMayManageScopeAsync(command.ActingRole, command.ActingPersonId, scope.Id))
        {
            return output.WithError(ScopeMessages.NotScopeOwner);
        }

        // AF-50c: looked up within the scope, so another scope's registration is simply not found.
        var pending = await pendingReader.Query()
            .FirstOrDefaultAsync(
                x => x.PublicId == command.RegistrationId && x.ScopeId == scope.Id, cancellationToken);

        if (pending is null)
        {
            return output.WithError(ScopeMessages.PendingRegistrationNotFound);
        }

        var removal = await pendingWriter.DeleteRangeAsync([pending.Id], cancellationToken);

        if (!removal.Success)
        {
            return removal.Errors.Contains(RelationalErrors.ConcurrencyMessage)
                ? output.WithError(ScopeMessages.PendingRegistrationNotFound)
                : output.WithErrors(removal.Errors);
        }

        if ((removal.Data?.Count() ?? 0) == 0)
        {
            return output.WithError(ScopeMessages.PendingRegistrationNotFound);
        }

        return output
            .WithData(new DiscardPendingRegistrationCommandOutput { Id = pending.PublicId })
            .WithMessage(ScopeMessages.PendingRegistrationDiscarded);
    }
}
