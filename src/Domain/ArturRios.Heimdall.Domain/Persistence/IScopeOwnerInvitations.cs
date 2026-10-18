using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;

namespace ArturRios.Heimdall.Domain.Persistence;

/// <summary>What <see cref="IScopeOwnerInvitations.OpenAsync" /> did: the pair's open invitation, and whether this call created it.</summary>
public sealed record ScopeOwnerInvitationOpening(ScopeOwnerInvitation Invitation, bool Created);

/// <summary>
///     The writes that end a scope owner invitation (FR-SC-17, FR-SC-18). Each end is one conditional
///     <c>UPDATE … WHERE</c> on an open invitation, as <see cref="IAtomicWrites" />'s writes are, so of any
///     number of racing answers, withdrawals and voids exactly one takes effect and every other is told so.
/// </summary>
public interface IScopeOwnerInvitations
{
    /// <summary>
    ///     Opens <paramref name="invitation" /> for its scope and invitee, or answers with the one already open
    ///     (<c>Created = false</c>). An expired, unended invitation of the pair is first ended at its expiry with
    ///     no outcome, since it still holds the pair's slot. Two simultaneous calls create one row between them.
    /// </summary>
    Task<ScopeOwnerInvitationOpening> OpenAsync(ScopeOwnerInvitation invitation, DateTime now);

    /// <summary>
    ///     Ends the invitation with <paramref name="outcome" /> — declined, withdrawn or voided — if it is still
    ///     open at <paramref name="now" />. <c>false</c> means it had already ended or expired, and nothing was
    ///     written. Acceptance writes an ownership too and is <see cref="TryAcceptAsync" />'s.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="outcome" /> is Accepted.</exception>
    Task<bool> TryEndAsync(ScopeOwnerInvitation invitation, ScopeOwnerInvitationOutcomes outcome, DateTime now);

    /// <summary>
    ///     In one transaction: ends the invitation as accepted — only while it is open, its scope is live and
    ///     its invitee <see cref="ScopeOwnerInvitation.EligibleInvitee" /> — and, only if that write took,
    ///     inserts the <c>SCOPE_OWNER</c> row (a no-op when it exists). <c>false</c> means nothing was written.
    /// </summary>
    Task<bool> TryAcceptAsync(ScopeOwnerInvitation invitation, DateTime now);

    /// <summary>
    ///     Anonymisation (NFR-20): voids every unended invitation whose invitee is among
    ///     <paramref name="personIds" />, answering with their public ids, and clears those persons as inviter
    ///     on every invitation.
    /// </summary>
    Task<IReadOnlyList<Guid>> ForgetPersonsAsync(IReadOnlyCollection<long> personIds, DateTime now);
}
