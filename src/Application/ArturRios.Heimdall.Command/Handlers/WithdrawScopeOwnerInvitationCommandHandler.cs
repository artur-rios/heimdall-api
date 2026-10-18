using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Domain.Persistence;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Heimdall.Shared.Services;
using ArturRios.Mediator.Command.Interfaces;
using ArturRios.Output;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Heimdall.Command.Handlers;

/// <summary>
///     Handles <see cref="WithdrawScopeOwnerInvitationCommand" /> (UC-58, FR-SC-15): an owner of the scope, or
///     a System Admin, ends an open invitation as withdrawn. All failures are returned as errors on the
///     <see cref="DataOutput{T}" /> rather than thrown.
/// </summary>
/// <remarks>
///     A missing or logically deleted scope is AF-58a and a caller who may not manage it AF-58b, checked
///     before the invitation is looked up so a non-owner learns nothing about which ids exist. The invitation
///     is looked up within the scope, so another scope's is not found (AF-58c), exactly like an ended, an
///     expired or an unknown one, or one that an answer ended between the read and the write.
/// </remarks>
public class WithdrawScopeOwnerInvitationCommandHandler(
    IAsyncReadOnlyRepository<Scope, long> scopeReader,
    IAsyncReadOnlyRepository<ScopeOwnerInvitation, long> invitationReader,
    IScopeOwnerInvitations invitations,
    IScopeOwnershipChecker scopeOwnership)
    : ICommandHandlerAsync<WithdrawScopeOwnerInvitationCommand, WithdrawScopeOwnerInvitationCommandOutput>
{
    public async Task<DataOutput<WithdrawScopeOwnerInvitationCommandOutput?>> HandleAsync(
        WithdrawScopeOwnerInvitationCommand command, CancellationToken cancellationToken = default)
    {
        var output = DataOutput<WithdrawScopeOwnerInvitationCommandOutput?>.New;

        // AF-58a.
        var scope = await scopeReader.Query()
            .FirstOrDefaultAsync(x => x.PublicId == command.ScopeId && !x.IsDeleted, cancellationToken);

        if (scope is null)
        {
            return output.WithError(ScopeMessages.ScopeNotFound);
        }

        // AF-58b.
        if (!await scopeOwnership.ActorMayManageScopeAsync(command.ActingRole, command.ActingPersonId, scope.Id))
        {
            return output.WithError(ScopeMessages.NotScopeOwner);
        }

        var now = DateTime.UtcNow;

        // AF-58c.
        var invitation = await invitationReader.Query()
            .Where(x => x.PublicId == command.InvitationId && x.ScopeId == scope.Id)
            .Where(ScopeOwnerInvitation.OpenAt(now))
            .FirstOrDefaultAsync(cancellationToken);

        if (invitation is null ||
            !await invitations.TryEndAsync(invitation, ScopeOwnerInvitationOutcomes.Withdrawn, now))
        {
            return output.WithError(ScopeInvitationMessages.InvitationNotOpen);
        }

        // UC-58 main flow: no message — the endpoint answers 204 with no body.
        return output.WithData(new WithdrawScopeOwnerInvitationCommandOutput { Id = invitation.PublicId });
    }
}
