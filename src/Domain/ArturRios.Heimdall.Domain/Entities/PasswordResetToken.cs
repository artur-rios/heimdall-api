using System.ComponentModel.DataAnnotations;
using ArturRios.Data.Relational.Core.Entities;

namespace ArturRios.Heimdall.Domain.Entities;

/// <summary>
///     A time-limited, single-use token issued for a password reset. Never addressed by its
///     internal <see cref="Entity.Id" />; the caller-facing reference is the random
///     <see cref="Token" /> string, so it has no <c>PublicId</c>.
/// </summary>
public class PasswordResetToken : Entity
{
    /// <summary>Foreign key to the associated <see cref="Person" /> (internal Id). Required.</summary>
    public long PersonId { get; set; }

    /// <summary>The reset token value — the caller-facing reference.</summary>
    [MaxLength(256)]
    public string Token { get; set; } = string.Empty;

    /// <summary>Expiration timestamp, after which the token is rejected.</summary>
    public DateTime ExpiresAt { get; set; }

    /// <summary>Whether the token has already been consumed.</summary>
    public bool Used { get; set; }

    // Navigation properties

    /// <summary>The person this token was issued for.</summary>
    public Person Person { get; set; } = null!;
}
