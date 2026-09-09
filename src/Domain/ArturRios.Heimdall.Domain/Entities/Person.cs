using System.ComponentModel.DataAnnotations;
using ArturRios.Data.Relational.Core.Entities;

namespace ArturRios.Heimdall.Domain.Entities;

/// <summary>
///     A registered identity. Has no direct scope attribute — its relationship to scopes is
///     derived from its role: a <c>User</c> belongs to exactly one scope (via <c>SCOPE_USER</c>),
///     a <c>ScopeAdmin</c> owns one or more scopes (via <c>SCOPE_OWNER</c>), and a
///     <c>SystemAdmin</c> belongs to no scope.
/// </summary>
public class Person : Entity
{
    /// <summary>
    ///     External identifier, generated on creation and used everywhere the person is addressed
    ///     from outside the database (API paths, response bodies, token claims). The internal
    ///     <see cref="Entity.Id" /> is never exposed to callers.
    /// </summary>
    public Guid PublicId { get; set; } = Guid.NewGuid();

    /// <summary>Full name. Required, max 200 characters.</summary>
    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    ///     Email address. Unique within the scope for <c>User</c>s (via <c>SCOPE_USER</c>);
    ///     unique system-wide for <c>ScopeAdmin</c>s and <c>SystemAdmin</c>s.
    /// </summary>
    [MaxLength(256)]
    public string Email { get; set; } = string.Empty;

    /// <summary>Hash computed from the password and <see cref="Salt" />.</summary>
    public byte[] PasswordHash { get; set; } = [];

    /// <summary>Randomly generated per person, used to hash the password.</summary>
    public byte[] Salt { get; set; } = [];

    /// <summary>Logical deletion flag. Logically deleted persons are excluded from default queries.</summary>
    public bool IsDeleted { get; set; }

    /// <summary>
    ///     When the record was logically deleted, or <c>null</c> while it is live. The retention
    ///     window that ends in anonymisation is measured from here (NFR-20).
    /// </summary>
    /// <remarks>
    ///     <see cref="UpdatedAt" /> cannot serve: it moves on any later write, so a record touched
    ///     after its deletion would have its window silently restarted. This one is written once, by
    ///     the logical deletion, and never again.
    /// </remarks>
    public DateTime? DeletedAt { get; set; }

    /// <summary>
    ///     Why the record was deleted (see <c>DeletionKinds</c>), or <c>null</c> while it is live.
    ///     Decides which of the schedule's two deadlines applies.
    /// </summary>
    public int? DeletionKind { get; set; }

    /// <summary>
    ///     When the record was anonymised, or <c>null</c> if it has not been. Set by the scheduled
    ///     pass, and the reason a second pass over the same row is a no-op rather than a repeated
    ///     overwrite.
    /// </summary>
    public DateTime? AnonymisedAt { get; set; }

    /// <summary>
    ///     When the data subject asked to be erased (UC-42), or <c>null</c> if they have not. Set
    ///     once, by the request, and never moved: the statutory deadline runs from the request, not
    ///     from any later event.
    /// </summary>
    public DateTime? ErasureRequestedAt { get; set; }

    /// <summary>
    ///     The deadline the erasure must not outlive, computed when the request is made.
    /// </summary>
    /// <remarks>
    ///     Stored rather than derived on read, and that is the point. GDPR Art. 12(3) attaches an
    ///     obligation at the moment of the request; recomputing it from the current configuration
    ///     would let a later change to the deadline move an obligation that had already attached —
    ///     forward, which is unlawful, or backward, which would silently mark existing requests
    ///     overdue.
    /// </remarks>
    public DateTime? ErasureDueAt { get; set; }

    /// <summary>
    ///     Why the erasure could not be carried out yet, or <c>null</c> if nothing blocks it. One of
    ///     the application's own canonical messages, never caller input.
    /// </summary>
    /// <remarks>
    ///     A blocked request is still a request: the deadline keeps running, and this is what makes
    ///     the reason visible to the administrator who has to clear it, rather than the request
    ///     failing and leaving no trace.
    /// </remarks>
    [MaxLength(500)]
    public string? ErasureBlockedReason { get; set; }

    /// <summary>Email verification status.</summary>
    public bool EmailVerified { get; set; }

    /// <summary>
    ///     Consecutive failed password attempts since the last successful login. Reset to zero on
    ///     success, and the counter <see cref="LockedOutUntil" /> is derived from.
    /// </summary>
    public int FailedLoginAttempts { get; set; }

    /// <summary>
    ///     When set and still in the future, login is refused regardless of the password supplied —
    ///     the per-account half of the brute-force defence, alongside the per-IP request limiter. The
    ///     refusal is UC-11's ordinary <c>InvalidCredentials</c>, so a lockout is not observable to a
    ///     caller who does not already know the password.
    /// </summary>
    public DateTime? LockedOutUntil { get; set; }

    /// <summary>Foreign key to the associated <c>Role</c> (internal Id). Required.</summary>
    public long RoleId { get; set; }

    /// <summary>
    ///     The scope a <c>User</c> belongs to (internal Id), or <c>null</c> for a <c>ScopeAdmin</c>
    ///     or <c>SystemAdmin</c>. A copy of <see cref="ScopeMembership" />'s <c>ScopeId</c>, not a
    ///     second source of truth: <c>SCOPE_USER</c> remains the relationship (§4.6) and every read
    ///     goes through it.
    /// </summary>
    /// <remarks>
    ///     This exists for one reason — FR-PE-09's per-scope rule cannot otherwise be enforced. The
    ///     scope lives in <c>SCOPE_USER</c> and the address in <c>PERSON</c>, and a PostgreSQL unique
    ///     index covers one table, so the rule was left to a check-then-insert that two concurrent
    ///     creates both pass. Carrying the scope here lets the index be written over columns this
    ///     table already has — <c>role_id</c>, <c>is_deleted</c>, <c>email</c> — with a condition
    ///     matching the application's check exactly, so nothing about the rule changes; only who
    ///     enforces it does.
    ///
    ///     Kept in step with <see cref="ScopeMembership" /> at the three places that write it:
    ///     UC-06 path a sets both, UC-23 and UC-08's role change clear both.
    /// </remarks>
    public long? ScopeId { get; set; }


    /// <summary>
    ///     The lawful basis this identity's data is processed on (see <c>LegalBases</c>), recorded
    ///     when the identity was created (GDPR Art. 5(2), LGPD Art. 8 §2).
    /// </summary>
    public int LegalBasis { get; set; }

    /// <summary>
    ///     The version of the privacy notice in force when the identity was created, or <c>null</c>
    ///     for identities predating the mechanism.
    /// </summary>
    /// <remarks>
    ///     Stored rather than looked up, for the same reason the erasure deadline is: what the
    ///     person was told is a fact about a moment, and reading the current version later would
    ///     answer a different question — what they would be told today.
    /// </remarks>
    [MaxLength(50)]
    public string? PrivacyNoticeVersion { get; set; }

    /// <summary>When the basis and notice version were recorded.</summary>
    public DateTime? BasisRecordedAt { get; set; }

    /// <summary>
    ///     When processing of this identity was restricted (GDPR Art. 18, LGPD Art. 18 III–IV), or
    ///     <c>null</c> if it is not restricted.
    /// </summary>
    /// <remarks>
    ///     Independent of <see cref="IsDeleted" />, and necessarily so. Deletion means the identity
    ///     is on its way out; restriction means it is disputed and must be preserved exactly as it
    ///     is while that is resolved. Overloading the deletion flag would make a restricted record
    ///     disappear from the administrator's view that has to resolve the dispute.
    /// </remarks>
    public DateTime? ProcessingRestrictedAt { get; set; }

    /// <summary>
    ///     Which of Art. 18(1)'s four grounds the restriction rests on (see
    ///     <c>RestrictionGrounds</c>), or <c>null</c> if not restricted.
    /// </summary>
    public int? RestrictionGround { get; set; }

    /// <summary>
    ///     When the subject was told the restriction was about to be lifted, or <c>null</c>.
    /// </summary>
    /// <remarks>
    ///     GDPR Art. 18(3) requires the subject be informed <em>before</em> a restriction imposed at
    ///     their request is lifted. This records that it happened, and the lift refuses to proceed
    ///     without it.
    /// </remarks>
    public DateTime? RestrictionLiftNotifiedAt { get; set; }


    /// <summary>Creation timestamp.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>Last update timestamp.</summary>
    public DateTime UpdatedAt { get; set; }

    // Navigation properties

    /// <summary>The role that classifies this person.</summary>
    public Role Role { get; set; } = null!;

    /// <summary>
    ///     Scope-ownership join rows. Non-empty for a <c>ScopeAdmin</c> (one per owned scope);
    ///     empty for a <c>User</c> or <c>SystemAdmin</c>.
    /// </summary>
    public ICollection<ScopeOwner> ScopeOwnerships { get; set; } = new List<ScopeOwner>();

    /// <summary>
    ///     Scope-membership join row. Present for a <c>User</c> (exactly one); <c>null</c> for a
    ///     <c>ScopeAdmin</c> or <c>SystemAdmin</c>.
    /// </summary>
    public ScopeUser? ScopeMembership { get; set; }

    /// <summary>Applications owned by this person.</summary>
    public ICollection<Application> OwnedApplications { get; set; } = new List<Application>();

    /// <summary>Password reset tokens issued for this person.</summary>
    public ICollection<PasswordResetToken> PasswordResetTokens { get; set; } = new List<PasswordResetToken>();

    /// <summary>Email verification tokens issued for this person.</summary>
    public ICollection<EmailVerificationToken> EmailVerificationTokens { get; set; } = new List<EmailVerificationToken>();

    /// <summary>
    ///     This person's two-factor authentication configuration (UC-36 – UC-40). <c>null</c> until
    ///     UC-36 initiates setup.
    /// </summary>
    public TwoFactorAuth? TwoFactorAuth { get; set; }
}
