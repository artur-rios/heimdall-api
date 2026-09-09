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
    public DateTime ExportedAt { get; set; }

    /// <summary>Who it is about, and the values that identify them.</summary>
    public required DataExportSubject Subject { get; set; }

    /// <summary>The state of the account's credentials, without the credentials.</summary>
    public required DataExportSecurity Security { get; set; }

    /// <summary>The lawful basis this data is held on, and what the subject was told.</summary>
    public required DataExportProcessing Processing { get; set; }

    /// <summary>Deletion and erasure state, where any applies.</summary>
    public DataExportErasure? Erasure { get; set; }

    /// <summary>Scopes the subject belongs to or owns, by public identifier.</summary>
    public IEnumerable<Guid> ScopeMemberships { get; set; } = [];

    /// <inheritdoc cref="ScopeMemberships" />
    public IEnumerable<Guid> ScopeOwnerships { get; set; } = [];

    /// <summary>Applications the subject owns.</summary>
    public IEnumerable<DataExportApplication> Applications { get; set; } = [];

    /// <summary>Every audit entry still attributed to the subject.</summary>
    /// <remarks>
    ///     Entries whose attribution has been cleared under NFR-21 are absent, and necessarily so:
    ///     once cleared, nothing connects them to this person, which is the whole point of clearing
    ///     them. <see cref="AuditEntriesTruncated" /> says whether the cap was reached.
    /// </remarks>
    public IEnumerable<DataExportAuditEntry> AuditEntries { get; set; } = [];

    /// <summary>
    ///     Whether more audit entries exist than this document carries. Disclosed rather than
    ///     silently truncated: a copy that quietly omits part of the data is not the copy Art. 15
    ///     asks for.
    /// </summary>
    public bool AuditEntriesTruncated { get; set; }

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
    public IEnumerable<DataExportWithheld> Withheld { get; set; } = [];

    /// <summary>Who else receives this data (GDPR Art. 15(1)(c), LGPD Art. 18 VII).</summary>
    public IEnumerable<DataExportRecipient> Recipients { get; set; } = [];

    /// <summary>How long each category is kept (GDPR Art. 15(1)(d)).</summary>
    public IEnumerable<DataExportRetention> Retention { get; set; } = [];
}

/// <summary>The identifying values held about the subject.</summary>
public class DataExportSubject
{
    /// <summary>Public identifier. The internal database key is never exposed (NFR-15).</summary>
    public Guid Id { get; set; }

    /// <summary>Whether the subject authenticates with a password or through Google.</summary>
    public string Kind { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public bool EmailVerified { get; set; }

    /// <summary>Role value, for a person; <c>null</c> for a Google User, who is always User-equivalent.</summary>
    public int? Role { get; set; }

    /// <summary>Google's stable subject identifier, for a Google User.</summary>
    public string? GoogleId { get; set; }

    /// <summary>Profile picture URL, for a Google User.</summary>
    public string? ProfilePictureUrl { get; set; }

    public bool IsDeleted { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>The state of the account's credentials, described rather than disclosed.</summary>
public class DataExportSecurity
{
    /// <summary>Whether a password is set. A Google User has none.</summary>
    public bool PasswordSet { get; set; }

    /// <summary>Whether two-factor authentication is active.</summary>
    public bool TwoFactorActive { get; set; }

    /// <summary>Whether the authenticator-app method is enabled.</summary>
    public bool TwoFactorAppEnabled { get; set; }

    /// <summary>Whether the email method is enabled.</summary>
    public bool TwoFactorEmailEnabled { get; set; }

    /// <summary>Recovery codes issued and not yet used.</summary>
    public int UnusedRecoveryCodes { get; set; }

    /// <summary>Consecutive failed sign-ins since the last success.</summary>
    public int FailedLoginAttempts { get; set; }

    /// <summary>When the account is locked out until, if it is.</summary>
    public DateTime? LockedOutUntil { get; set; }
}

/// <summary>The lawful basis and the notice the subject was shown.</summary>
public class DataExportProcessing
{
    /// <summary>The recorded basis value (see <c>LegalBases</c>).</summary>
    public int LegalBasis { get; set; }

    /// <summary>The basis in words, so the document is readable without the enum.</summary>
    public string LegalBasisName { get; set; } = string.Empty;

    /// <summary>The privacy notice version in force when the identity was created.</summary>
    public string? PrivacyNoticeVersion { get; set; }

    /// <summary>When the basis was recorded.</summary>
    public DateTime? RecordedAt { get; set; }
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
    public string Name { get; set; } = string.Empty;
    public Guid ScopeId { get; set; }
    public bool IsDeleted { get; set; }
}

/// <summary>One audit entry attributed to the subject.</summary>
public class DataExportAuditEntry
{
    public Guid Id { get; set; }
    public string Action { get; set; } = string.Empty;
    public Guid? TargetId { get; set; }
    public bool Succeeded { get; set; }
    public string? FailureReason { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>A value held but not reproduced, and why.</summary>
public class DataExportWithheld
{
    public string Value { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
}

/// <summary>Somebody else this data reaches.</summary>
public class DataExportRecipient
{
    public string Name { get; set; } = string.Empty;
    public string Purpose { get; set; } = string.Empty;
    public string Location { get; set; } = string.Empty;
}

/// <summary>How long a category of data is kept.</summary>
public class DataExportRetention
{
    public string Category { get; set; } = string.Empty;
    public string Period { get; set; } = string.Empty;
}
