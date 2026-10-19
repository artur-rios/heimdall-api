using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Command.Services;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Domain.Persistence;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Mediator.Command.Interfaces;
using ArturRios.Output;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Heimdall.Command.Handlers;

/// <summary>
///     Handles <see cref="DeclineScopeOwnerInvitationCommand" /> (UC-61, FR-SC-17): the invitee ends an open
///     invitation as declined, and the inviter is told. Any other caller, and an ended or expired invitation,
///     get AF-61a's single answer, as for UC-60. All failures are returned as errors on the
///     <see cref="DataOutput{T}" /> rather than thrown.
/// </summary>
public class DeclineScopeOwnerInvitationCommandHandler(
    IAsyncReadOnlyRepository<Person, long> personReader,
    IAsyncReadOnlyRepository<Scope, long> scopeReader,
    IAsyncReadOnlyRepository<ScopeOwnerInvitation, long> invitationReader,
    IScopeOwnerInvitations invitations,
    IScopeInvitationNoticeSender notices)
    : ICommandHandlerAsync<DeclineScopeOwnerInvitationCommand, DeclineScopeOwnerInvitationCommandOutput>
{
    public async Task<DataOutput<DeclineScopeOwnerInvitationCommandOutput?>> HandleAsync(
        DeclineScopeOwnerInvitationCommand command, CancellationToken cancellationToken = default)
    {
        var output = DataOutput<DeclineScopeOwnerInvitationCommandOutput?>.New;
        var now = DateTime.UtcNow;

        // AF-61a.
        var found = await InviteeInvitationLookup.FindAsync(
            personReader, invitationReader, command.ActingPersonId, command.InvitationId, now, cancellationToken);

        if (found is null)
        {
            return output.WithError(ScopeInvitationMessages.InvitationNotOpen);
        }

        var (invitee, invitation) = found.Value;

        if (!await invitations.TryEndAsync(invitation, ScopeOwnerInvitationOutcomes.Declined, now))
        {
            return output.WithError(ScopeInvitationMessages.InvitationNotOpen);
        }

        var scope = await scopeReader.Query()
            .FirstOrDefaultAsync(x => x.Id == invitation.ScopeId, cancellationToken);

        if (scope is not null &&
            await InviteeInvitationLookup.InviterToTellAsync(personReader, invitation, cancellationToken) is { } inviter)
        {
            await notices.SendAnswerAsync(inviter.Email, invitee.Name, scope.Name, accepted: false);
        }

        return output
            .WithData(new DeclineScopeOwnerInvitationCommandOutput { Id = invitation.PublicId })
            .WithMessage(ScopeInvitationMessages.InvitationDeclined);
    }
}
