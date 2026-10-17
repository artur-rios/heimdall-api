using System.Linq.Expressions;
using ArturRios.Data.Relational.Core.Entities;
using ArturRios.Heimdall.Domain.Enums;

namespace ArturRios.Heimdall.Domain.Entities;

/// <summary>
///     An invitation to co-own a scope (UC-21, FR-SC-14): a Scope Admin asked for an existing administrator
///     to become an owner, and the ownership starts only when that person accepts it, signed in as
///     themselves (UC-60). It ends exactly once — accepted, declined, withdrawn or voided — or runs out at
///     <see cref="ExpiresAt" />.
/// </summary>
/// <remarks>
///     At most one invitation per scope and invitee is unended at a time, which a partial unique index
///     holds. An expired row still counts until something closes it, so opening a new one first closes an
///     expired one with <see cref="EndedAt" /> at its expiry and no <see cref="Outcome" />.
/// </remarks>
public class ScopeOwnerInvitation : Entity<long>
{
    /// <summary>
    ///     How the scope's owners withdraw it (UC-58) and the invitee answers it (UC-60, UC-61). Random, so it
    ///     discloses nothing about the people involved.
    /// </summary>
    public Guid PublicId { get; set; } = Guid.NewGuid();

    /// <summary>Foreign key to the scope the invitee is asked to co-own (internal Id).</summary>
    public long ScopeId { get; set; }

    /// <summary>Foreign key to the invited administrator (internal Id); only they may answer.</summary>
    public long InviteePersonId { get; set; }

    /// <summary>
    ///     Foreign key to the administrator who invited (internal Id). Cleared when they are anonymised, and
    ///     set to null by the database when they are hard-deleted.
    /// </summary>
    public long? InvitedByPersonId { get; set; }

    public DateTime CreatedAt { get; set; }

    /// <summary>When it stops being acceptable (<c>HEIMDALL_SCOPE_INVITATION_LIFETIME_IN_SECONDS</c> after creation).</summary>
    public DateTime ExpiresAt { get; set; }

    /// <summary>When it ended; <c>null</c> while it has not.</summary>
    public DateTime? EndedAt { get; set; }

    /// <summary>How it ended; <c>null</c> while open, and for one that ended by expiry.</summary>
    public ScopeOwnerInvitationOutcomes? Outcome { get; set; }

    public Scope Scope { get; set; } = null!;

    public Person Invitee { get; set; } = null!;

    public Person? InvitedBy { get; set; }

    /// <summary>
    ///     Open at <paramref name="now" />: not ended, and its expiry still ahead. An expression, so a query
    ///     and a conditional write ask exactly the same question.
    /// </summary>
    public static Expression<Func<ScopeOwnerInvitation, bool>> OpenAt(DateTime now) =>
        invitation => invitation.EndedAt == null && invitation.ExpiresAt > now;
}
