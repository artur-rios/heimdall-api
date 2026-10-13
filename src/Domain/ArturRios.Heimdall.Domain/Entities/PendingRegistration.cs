using System.ComponentModel.DataAnnotations;
using ArturRios.Data.Relational.Core.Entities;

namespace ArturRios.Heimdall.Domain.Entities;

/// <summary>
///     A self-registration (UC-47) waiting for its address to be confirmed. Not a person: nothing can
///     sign in with it, which is what keeps register-then-sign-in from revealing whether an address is
///     taken. Confirming it through the emailed link (UC-14) creates the verified <c>User</c> and
///     removes it; an unconfirmed one expires and is purged with the other single-use tokens (NFR-19).
/// </summary>
public class PendingRegistration : Entity<long>
{
    /// <summary>Foreign key to the scope registered into (internal Id).</summary>
    public long ScopeId { get; set; }

    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    public string Email { get; set; } = string.Empty;

    /// <summary>The password hash computed on the request path; the plaintext was never stored.</summary>
    public byte[] PasswordHash { get; set; } = [];

    public byte[] Salt { get; set; } = [];

    /// <summary>SHA-256 of the mailed token, as lowercase hex, through <c>SingleUseTokenHash</c>.</summary>
    [MaxLength(64)]
    public string TokenHash { get; set; } = string.Empty;

    public DateTime ExpiresAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public Scope Scope { get; set; } = null!;
}
