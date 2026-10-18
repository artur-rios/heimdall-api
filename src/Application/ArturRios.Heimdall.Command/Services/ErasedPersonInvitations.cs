using ArturRios.Heimdall.Command.Auditing;
using ArturRios.Heimdall.Domain.Persistence;
using Microsoft.Extensions.Logging;

namespace ArturRios.Heimdall.Command.Services;

/// <summary>
///     Forgets erased persons in the scope owner invitations (FR-SC-17): voids their unended invitations,
///     clears them as the inviter of every other, and records each void in the audit trail
///     (<c>ScopeOwnerInvitationVoided</c>, NFR-09) on the invitation it ended.
/// </summary>
/// <remarks>
///     <para>
///         Shared by the scheduled anonymisation pass (NFR-20) and the re-application of erasures (NFR-25),
///         so the two cannot disagree about what an erased person's invitations become.
///     </para>
///     <para>
///         <b>Why the voids are audited here, and at once.</b> <see cref="IScopeOwnerInvitations.ForgetPersonsAsync" />
///         commits on its own. Left to the run's outcome, a later step failing would lose the entries for
///         good: the decorator records a refused run's refusal only, and a retry finds the invitations
///         already ended, with no id left to return. Like the decorator, a failed audit write is logged and
///         the run goes on.
///     </para>
/// </remarks>
public static class ErasedPersonInvitations
{
    /// <returns>The public ids of the invitations voided, in no particular order.</returns>
    public static async Task<IReadOnlyList<Guid>> ForgetAsync(
        IScopeOwnerInvitations invitations,
        IAuditLogWriter auditLog,
        ILogger logger,
        IReadOnlyCollection<long> personIds,
        DateTime now)
    {
        if (personIds.Count == 0)
        {
            return [];
        }

        var voided = await invitations.ForgetPersonsAsync(personIds, now);

        foreach (var invitationId in voided)
        {
            try
            {
                await auditLog.WriteAsync(ScopeOwnerInvitationAuditActions.Voided, invitationId, true, null);
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Failed to audit the void of invitation {InvitationId}", invitationId);
            }
        }

        return voided;
    }
}
