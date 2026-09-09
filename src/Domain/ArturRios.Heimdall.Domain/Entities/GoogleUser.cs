using System.ComponentModel.DataAnnotations;
using ArturRios.Data.Relational.Core.Entities;

namespace ArturRios.Heimdall.Domain.Entities;

/// <summary>
///     A registered identity authenticated via Google Sign-In rather than a password. Always
///     equivalent to the <c>User</c> role and belongs to exactly one <see cref="Scope" />. Stored
///     in its own table with a direct <see cref="ScopeId" /> instead of going through the
///     owner/user join tables, since it never needs ownership or multi-scope semantics. Has no
///     <c>PasswordHash</c>, <c>Salt</c>, or <c>RoleId</c> — authentication is delegated to Google.
/// </summary>
public class GoogleUser : Entity
{
    /// <summary>
    ///     External identifier, generated on creation and used everywhere the Google User is
    ///     addressed from outside the database (API paths, response bodies, token claims). The
    ///     internal <see cref="Entity.Id" /> is never exposed to callers.
    /// </summary>
    public Guid PublicId { get; set; } = Guid.NewGuid();

    /// <summary>Google's stable <c>sub</c> claim. Required, unique within the scope.</summary>
    [MaxLength(255)]
    public string GoogleId { get; set; } = string.Empty;

    /// <summary>Full name, from Google's <c>name</c> claim. Max 200 characters.</summary>
    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    ///     Email address, from Google's <c>email</c> claim. Required, unique within the scope,
    ///     considered jointly with <c>User</c> persons' emails in that scope.
    /// </summary>
    [MaxLength(256)]
    public string Email { get; set; } = string.Empty;

    /// <summary>Email verification status, from Google's <c>email_verified</c> claim.</summary>
    public bool EmailVerified { get; set; }

    /// <summary>Optional profile picture URL, from Google's <c>picture</c> claim.</summary>
    [MaxLength(2048)]
    public string? ProfilePictureUrl { get; set; }

    /// <summary>Logical deletion flag. Logically deleted Google Users are excluded from default queries.</summary>
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

    /// <summary>Foreign key to the associated <see cref="Scope" /> (internal Id). Required.</summary>
    public long ScopeId { get; set; }


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

    /// <summary>The scope this Google User belongs to.</summary>
    public Scope Scope { get; set; } = null!;
}
