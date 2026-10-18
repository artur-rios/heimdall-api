using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Heimdall.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Heimdall.Command.Services;

/// <summary>
///     The reads UC-60 and UC-61 share: the caller's own open invitation, and the inviter who should hear the
///     answer.
/// </summary>
public static class InviteeInvitationLookup
{
    /// <summary>
    ///     The open invitation <paramref name="invitationId" />, with its invitee, if and only if it was made to
    ///     the caller; otherwise <c>null</c>. An unknown id, an ended or expired invitation and somebody else's
    ///     all give <c>null</c>, so inviter, invitee and outsider cannot tell them apart (AF-60a, AF-61a).
    /// </summary>
    public static async Task<(Person Invitee, ScopeOwnerInvitation Invitation)?> FindAsync(
        IAsyncReadOnlyRepository<Person, long> personReader,
        IAsyncReadOnlyRepository<ScopeOwnerInvitation, long> invitationReader,
        Guid actingPersonId,
        Guid invitationId,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var invitee = await personReader.Query()
            .FirstOrDefaultAsync(x => x.PublicId == actingPersonId, cancellationToken);

        if (invitee is null)
        {
            return null;
        }

        var invitation = await invitationReader.Query()
            .Where(x => x.PublicId == invitationId && x.InviteePersonId == invitee.Id)
            .Where(ScopeOwnerInvitation.OpenAt(now))
            .FirstOrDefaultAsync(cancellationToken);

        return invitation is null ? null : (invitee, invitation);
    }

    /// <summary>
    ///     The inviter, when there is still somebody to tell: not cleared by anonymisation or a hard delete, and
    ///     not logically deleted or anonymised. <c>null</c> means no answer notice is sent.
    /// </summary>
    public static async Task<Person?> InviterToTellAsync(
        IAsyncReadOnlyRepository<Person, long> personReader,
        ScopeOwnerInvitation invitation,
        CancellationToken cancellationToken)
    {
        if (invitation.InvitedByPersonId is not { } inviterId)
        {
            return null;
        }

        return await personReader.Query()
            .FirstOrDefaultAsync(x => x.Id == inviterId && !x.IsDeleted && x.AnonymisedAt == null, cancellationToken);
    }
}
