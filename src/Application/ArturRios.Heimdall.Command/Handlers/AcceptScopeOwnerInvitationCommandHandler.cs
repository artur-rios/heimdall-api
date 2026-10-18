using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Heimdall.Command.Auditing;
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
using Microsoft.Extensions.Logging;

namespace ArturRios.Heimdall.Command.Handlers;

/// <summary>
///     Handles <see cref="AcceptScopeOwnerInvitationCommand" /> (UC-60, FR-SC-17): the invitee, signed in as
///     themselves, accepts an open invitation, which ends it as accepted and makes them an owner of the scope
///     in one transaction; the inviter is then told. All failures are returned as errors on the
///     <see cref="DataOutput{T}" /> rather than thrown.
/// </summary>
/// <remarks>
///     <para>
///         <b>One answer for every refusal (AF-60a).</b> An unknown id, somebody else's invitation, an ended or
///         expired one, and one that a withdrawal or another answer ended between the read and the write all
///         answer "The invitation was not found or is no longer open." The inviter accepting their own
///         invitation is told the same as an outsider.
///     </para>
///     <para>
///         <b>An invitation that can no longer be honoured is voided (AF-60b).</b> The scope must still be live
///         and the invitee <see cref="ScopeOwnerInvitation.EligibleInvitee" />. If not, the invitation ends as
///         voided, the void is audited with the invitation as target, and the answer is AF-60a's. The
///         acceptance's own write re-checks both conditions, so a change landing after this check cannot slip
///         through; when that write is refused, the invitation is read again and, if it is still open but no
///         longer honourable, voided the same way.
///     </para>
///     <para>
///         The acceptance (<see cref="IScopeOwnerInvitations.TryAcceptAsync" />) opens its own transaction, so
///         this handler opens none around it.
///     </para>
/// </remarks>
public class AcceptScopeOwnerInvitationCommandHandler(
    IAsyncReadOnlyRepository<Person, long> personReader,
    IAsyncReadOnlyRepository<Scope, long> scopeReader,
    IAsyncReadOnlyRepository<ScopeOwnerInvitation, long> invitationReader,
    IScopeOwnerInvitations invitations,
    IScopeInvitationNoticeSender notices,
    IAuditLogWriter auditLog,
    ILogger<AcceptScopeOwnerInvitationCommandHandler> logger)
    : ICommandHandlerAsync<AcceptScopeOwnerInvitationCommand, AcceptScopeOwnerInvitationCommandOutput>
{
    private static readonly Func<Person, bool> EligibleInvitee = ScopeOwnerInvitation.EligibleInvitee.Compile();

    public async Task<DataOutput<AcceptScopeOwnerInvitationCommandOutput?>> HandleAsync(
        AcceptScopeOwnerInvitationCommand command, CancellationToken cancellationToken = default)
    {
        var output = DataOutput<AcceptScopeOwnerInvitationCommandOutput?>.New;
        var now = DateTime.UtcNow;

        // AF-60a.
        var found = await InviteeInvitationLookup.FindAsync(
            personReader, invitationReader, command.ActingPersonId, command.InvitationId, now, cancellationToken);

        if (found is null)
        {
            return output.WithError(ScopeInvitationMessages.InvitationNotOpen);
        }

        var (invitee, invitation) = found.Value;

        var scope = await scopeReader.Query()
            .FirstOrDefaultAsync(x => x.Id == invitation.ScopeId, cancellationToken);

        // AF-60b. EligibleInvitee covers the person only; the scope's liveness is checked beside it.
        if (scope is null || scope.IsDeleted || !EligibleInvitee(invitee))
        {
            await VoidAsync(invitation, now);

            return output.WithError(ScopeInvitationMessages.InvitationNotOpen);
        }

        // UC-60 step 3: ends the invitation and inserts the ownership together, or neither (AF-60a).
        if (!await invitations.TryAcceptAsync(invitation, now))
        {
            // AF-60b landing between the check above and the write: void it if it is still open.
            if (await NoLongerHonourableAsync(invitation, now, cancellationToken))
            {
                await VoidAsync(invitation, now);
            }

            return output.WithError(ScopeInvitationMessages.InvitationNotOpen);
        }

        if (await InviteeInvitationLookup.InviterToTellAsync(personReader, invitation, cancellationToken) is { } inviter)
        {
            await notices.SendAnswerAsync(inviter.Email, invitee.Name, scope.Name, accepted: true);
        }

        return output
            .WithData(new AcceptScopeOwnerInvitationCommandOutput { Id = invitation.PublicId, ScopeId = scope.PublicId })
            .WithMessage(ScopeInvitationMessages.InvitationAccepted);
    }

    /// <summary>
    ///     After a refused acceptance, reads the invitation again: <c>true</c> when it is still open and its scope
    ///     is no longer live or its invitee no longer <see cref="ScopeOwnerInvitation.EligibleInvitee" />;
    ///     <c>false</c> when something else ended it, a withdrawal or another answer, which stands. The checks
    ///     run in the query, so they see the stored rows rather than the instances read earlier.
    /// </summary>
    private async Task<bool> NoLongerHonourableAsync(
        ScopeOwnerInvitation invitation, DateTime now, CancellationToken cancellationToken)
    {
        var stillOpen = await invitationReader.Query()
            .Where(x => x.Id == invitation.Id)
            .Where(ScopeOwnerInvitation.OpenAt(now))
            .AnyAsync(cancellationToken);

        if (!stillOpen)
        {
            return false;
        }

        var scopeLive = await scopeReader.Query()
            .AnyAsync(x => x.Id == invitation.ScopeId && !x.IsDeleted, cancellationToken);
        var inviteeEligible = await personReader.Query()
            .Where(x => x.Id == invitation.InviteePersonId)
            .Where(ScopeOwnerInvitation.EligibleInvitee)
            .AnyAsync(cancellationToken);

        return !scopeLive || !inviteeEligible;
    }

    /// <summary>
    ///     Ends the invitation as voided and, only if that write took effect, records the void (NFR-09). The
    ///     decorator records this request's own refusal; the void is a second event, with the invitation as
    ///     target. Like the decorator, a failed audit write is logged and the answer stands.
    /// </summary>
    private async Task VoidAsync(ScopeOwnerInvitation invitation, DateTime now)
    {
        if (!await invitations.TryEndAsync(invitation, ScopeOwnerInvitationOutcomes.Voided, now))
        {
            return;
        }

        try
        {
            await auditLog.WriteAsync(ScopeOwnerInvitationAuditActions.Voided, invitation.PublicId, true, null);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to audit the void of invitation {InvitationId}", invitation.PublicId);
        }
    }
}
