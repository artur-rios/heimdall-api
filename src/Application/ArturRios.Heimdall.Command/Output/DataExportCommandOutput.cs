using ArturRios.Mediator.Command;

namespace ArturRios.Heimdall.Command.Output;

/// <summary>
///     Everything Heimdall holds about one data subject (UC-41), in one structured document.
/// </summary>
/// <remarks>
///     Art. 15 asks for a copy of the personal data <em>and</em> the context that makes it
///     intelligible — the purposes, the recipients, the retention periods, the rights. A dump of
///     table rows would satisfy the first half and none of the second, so the sections after
///     <see cref="AuditEntries" /> are part of the answer rather than decoration.
/// </remarks>
public class DataExportCommandOutput : CommandOutput
{
    /// <summary>When this copy was produced.</summary>
    public DateTime ExportedAt { get; init; }

    /// <summary>Who it is about, and the values that identify them.</summary>
    public required DataExportSubject Subject { get; init; }

    /// <summary>The state of the account's credentials, without the credentials.</summary>
    public required DataExportSecurity Security { get; init; }

    /// <summary>The lawful basis this data is held on, and what the subject was told.</summary>
    public required DataExportProcessing Processing { get; init; }

    /// <summary>Deletion and erasure state, where any applies.</summary>
    public DataExportErasure? Erasure { get; init; }

    /// <summary>Scopes the subject belongs to or owns, by public identifier.</summary>
    public IEnumerable<Guid> ScopeMemberships { get; init; } = [];

    /// <inheritdoc cref="ScopeMemberships" />
    public IEnumerable<Guid> ScopeOwnerships { get; set; } = [];

    /// <summary>Applications the subject owns.</summary>
    public IEnumerable<DataExportApplication> Applications { get; init; } = [];

    /// <summary>Every audit entry still attributed to the subject, as its actor or as its target.</summary>
    /// <remarks>
    ///     Entries whose attribution has been cleared under NFR-21 are absent, and necessarily so:
    ///     once cleared, nothing connects them to this person, which is the whole point of clearing
    ///     them. <see cref="AuditEntriesTruncated" /> says whether the cap was reached.
    /// </remarks>
    public IEnumerable<DataExportAuditEntry> AuditEntries { get; init; } = [];

    /// <summary>
    ///     Whether more audit entries exist than this document carries. Disclosed rather than
    ///     silently truncated: a copy that quietly omits part of the data is not the copy Art. 15
    ///     asks for.
    /// </summary>
    public bool AuditEntriesTruncated { get; init; }

    /// <summary>
    ///     Values held about the subject that are deliberately not reproduced here, each with the
    ///     reason.
    /// </summary>
    /// <remarks>
    ///     Art. 15(4) says the right to a copy must not adversely affect others, and reproducing a
    ///     password hash or a TOTP secret would hand an attacker holding a stolen token the material
    ///     to attack the account offline. They are named rather than silently omitted, so the
    ///     subject knows what exists.
    /// </remarks>
    public IEnumerable<DataExportWithheld> Withheld { get; init; } = [];

    /// <summary>Who else receives this data (GDPR Art. 15(1)(c), LGPD Art. 18 VII).</summary>
    public IEnumerable<DataExportRecipient> Recipients { get; init; } = [];

    /// <summary>
    ///     Where data that did not come from the subject came from instead (GDPR Art. 14(2)(f) and
    ///     Art. 15(1)(g), LGPD Art. 9).
    /// </summary>
    /// <remarks>
    ///     Separate from <see cref="Recipients" /> because the direction is opposite and conflating
    ///     them misleads in both directions. Google is the clearest case: it is where a Google
    ///     User's name and address came from, and nothing about them is ever sent to it — the ID
    ///     token is validated offline against cached public certificates.
    /// </remarks>
    public IEnumerable<DataExportSource> Sources { get; init; } = [];

    /// <summary>How long each category is kept (GDPR Art. 15(1)(d)).</summary>
    public IEnumerable<DataExportRetention> Retention { get; init; } = [];
}

/// <summary>The identifying values held about the subject.</summary>
public class DataExportSubject
{
    /// <summary>Public identifier. The internal database key is never exposed (NFR-15).</summary>
    public Guid Id { get; init; }

    /// <summary>Whether the subject authenticates with a password or through Google.</summary>
    public string Kind { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;
    public string Email { get; init; } = string.Empty;
    public bool EmailVerified { get; init; }

    /// <summary>Role value, for a person; <c>null</c> for a Google User, who is always User-equivalent.</summary>
    public int? Role { get; init; }

    /// <summary>Google's stable subject identifier, for a Google User.</summary>
    public string? GoogleId { get; init; }

    /// <summary>Profile picture URL, for a Google User.</summary>
    public string? ProfilePictureUrl { get; init; }

    public bool IsDeleted { get; init; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>The state of the account's credentials, described rather than disclosed.</summary>
public class DataExportSecurity
{
    /// <summary>Whether a password is set. A Google User has none.</summary>
    public bool PasswordSet { get; init; }

    /// <summary>Whether two-factor authentication is active.</summary>
    public bool TwoFactorActive { get; init; }

    /// <summary>Whether the authenticator-app method is enabled.</summary>
    public bool TwoFactorAppEnabled { get; init; }

    /// <summary>Whether the email method is enabled.</summary>
    public bool TwoFactorEmailEnabled { get; init; }

    /// <summary>Recovery codes issued and not yet used.</summary>
    public int UnusedRecoveryCodes { get; init; }

    /// <summary>Consecutive failed sign-ins since the last success.</summary>
    public int FailedLoginAttempts { get; set; }

    /// <summary>When the account is locked out until, if it is.</summary>
    public DateTime? LockedOutUntil { get; set; }

    /// <summary>Failed second factors at sign-in since the last successful one (FR-AU-12).</summary>
    public int FailedSecondFactorAttempts { get; init; }

    /// <summary>When second-factor verification is locked until, if it is or was (FR-AU-12).</summary>
    public DateTime? SecondFactorLockedOutUntil { get; init; }

    /// <summary>Second-factor locks since the last successful second factor, which set the next one's length.</summary>
    public int SecondFactorLockouts { get; init; }

    /// <summary>Failed re-authentications in the current window (FR-AU-13).</summary>
    public int FailedReauthenticationAttempts { get; init; }

    /// <summary>When the current re-authentication window began, if one is open (FR-AU-13).</summary>
    public DateTime? ReauthenticationWindowStartedAt { get; init; }

    /// <summary>When re-authentication is paused until, if it is or was (FR-AU-13).</summary>
    public DateTime? ReauthenticationPausedUntil { get; init; }

    /// <summary>Account emails the person's own requests had sent in the current window (FR-PE-16).</summary>
    public int AccountEmailsSent { get; init; }

    /// <summary>When the current account-email window ends, if one was opened (FR-PE-16).</summary>
    public DateTime? AccountEmailWindowEndsAt { get; init; }

    /// <summary>When the last of those account emails was sent, which the cooldown counts from (FR-PE-16).</summary>
    public DateTime? AccountEmailLastSentAt { get; init; }
}

/// <summary>The lawful basis and the notice the subject was shown.</summary>
public class DataExportProcessing
{
    /// <summary>The recorded basis value (see <c>LegalBases</c>).</summary>
    public int LegalBasis { get; init; }

    /// <summary>The basis in words, so the document is readable without the enum.</summary>
    public string LegalBasisName { get; init; } = string.Empty;

    /// <summary>The privacy notice version in force when the identity was created.</summary>
    public string? PrivacyNoticeVersion { get; init; }

    /// <summary>When the basis was recorded.</summary>
    public DateTime? RecordedAt { get; init; }
}

/// <summary>Deletion and erasure state.</summary>
public class DataExportErasure
{
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAt { get; set; }
    public int? DeletionKind { get; set; }
    public DateTime? AnonymisedAt { get; set; }
    public DateTime? ErasureRequestedAt { get; set; }
    public DateTime? ErasureDueAt { get; set; }
    public string? ErasureBlockedReason { get; set; }
}

/// <summary>An application the subject owns.</summary>
public class DataExportApplication
{
    public Guid Id { get; set; }
    public string Name { get; init; } = string.Empty;
    public Guid ScopeId { get; set; }
    public bool IsDeleted { get; set; }
}

/// <summary>One audit entry the subject acted in, or was the target of.</summary>
public class DataExportAuditEntry
{
    /// <summary><see cref="Involvement" /> of an entry the subject made.</summary>
    public const string AsActor = "Actor";

    /// <summary><see cref="Involvement" /> of an entry someone else made about the subject.</summary>
    public const string AsTarget = "Target";

    public Guid Id { get; set; }

    /// <summary>
    ///     <see cref="AsActor" /> when the subject made the request, <see cref="AsTarget" /> when it
    ///     was made about them by another identity.
    /// </summary>
    /// <remarks>
    ///     The other identity is never named in a <see cref="AsTarget" /> entry: it is data about the
    ///     subject, which Art. 15 covers, and the actor's identity is not, which Art. 15(4) protects.
    /// </remarks>
    public string Involvement { get; init; } = AsActor;
    public string Action { get; init; } = string.Empty;
    public Guid? TargetId { get; set; }
    public bool Succeeded { get; set; }
    public string? FailureReason { get; init; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>A value held but not reproduced, and why.</summary>
public class DataExportWithheld
{
    public string Value { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
}

/// <summary>Somebody else this data reaches.</summary>
public class DataExportRecipient
{
    public string Name { get; init; } = string.Empty;
    public string Purpose { get; init; } = string.Empty;
    public string Location { get; init; } = string.Empty;
}

/// <summary>Somewhere data about the subject came from, other than the subject.</summary>
public class DataExportSource
{
    public string Name { get; init; } = string.Empty;

    /// <summary>What came from there.</summary>
    public string Provides { get; init; } = string.Empty;

    /// <summary>Whether anything about the subject is sent back to it.</summary>
    public string DataSentThere { get; init; } = string.Empty;
}

/// <summary>How long a category of data is kept.</summary>
public class DataExportRetention
{
    public string Category { get; init; } = string.Empty;
    public string Period { get; init; } = string.Empty;
}
