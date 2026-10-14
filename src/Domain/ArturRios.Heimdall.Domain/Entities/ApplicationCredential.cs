using System.ComponentModel.DataAnnotations;
using System.Linq.Expressions;
using ArturRios.Data.Relational.Core.Entities;

namespace ArturRios.Heimdall.Domain.Entities;

/// <summary>
///     A secret an <see cref="Application" /> exchanges for an application token (UC-53, UC-56,
///     FR-AP-10). Only the secret's SHA-256 is stored; the secret itself is shown once, in UC-53's
///     response, and never again.
/// </summary>
/// <remarks>
///     <para>
///         An application holds at most <see cref="MaxLive" /> live credentials, so a new one can be
///         deployed before the old one is revoked (FR-AP-11). Each is revoked on its own (UC-55) and
///         may carry an expiry.
///     </para>
///     <para>
///         No personal data: the row names an application, a hash and four instants. Logically
///         deleting the application or its scope leaves the rows alone — UC-56 and UC-57 refuse a
///         deleted application or scope, so its credentials are inert — and hard-deleting the
///         application removes them by cascade. Revoked and expired rows are purged after the
///         single-use-token grace by the token retention pass.
///     </para>
/// </remarks>
public class ApplicationCredential : Entity<long>
{
    /// <summary>The most live credentials one application may hold at once (FR-AP-11).</summary>
    public const int MaxLive = 2;

    /// <summary>
    ///     External identifier — the credential's reference in UC-54's listing, UC-55's route, the
    ///     token's <c>credentialId</c> claim and the audit trail. The internal
    ///     <see cref="Entity{TKey}.Id" /> is never exposed.
    /// </summary>
    public Guid PublicId { get; set; } = Guid.NewGuid();

    /// <summary>Foreign key to the owning <see cref="Application" /> (internal Id). Required.</summary>
    public long ApplicationId { get; set; }

    /// <summary>
    ///     SHA-256 of the secret, as lowercase hex (<c>ApplicationCredentialSecret.HashOf</c>). The
    ///     secret itself is never stored: whoever could read this table could otherwise authenticate
    ///     as every application (Threat Model TH-33).
    /// </summary>
    [MaxLength(64)]
    public string SecretHash { get; set; } = string.Empty;

    /// <summary>When the credential was issued (UC-53).</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>When the credential stops being accepted, or <c>null</c> for never.</summary>
    public DateTime? ExpiresAt { get; set; }

    /// <summary>When the credential was revoked (UC-55), or <c>null</c> while it has not been.</summary>
    public DateTime? RevokedAt { get; set; }

    /// <summary>When a token was last issued from this credential (UC-56), or <c>null</c> if never.</summary>
    public DateTime? LastUsedAt { get; set; }

    // Navigation properties

    /// <summary>The application this credential authenticates.</summary>
    public Application Application { get; set; } = null!;

    /// <summary>
    ///     The one definition of a live credential — not revoked, and either without expiry or
    ///     expiring after <paramref name="now" /> — as an expression, so a query can filter on it in
    ///     the database.
    /// </summary>
    public static Expression<Func<ApplicationCredential, bool>> LiveAt(DateTime now) =>
        credential => credential.RevokedAt == null &&
                      (credential.ExpiresAt == null || credential.ExpiresAt > now);

    /// <summary><see cref="LiveAt" />, evaluated for this instance.</summary>
    public bool IsLiveAt(DateTime now) => LiveAt(now).Compile()(this);
}
