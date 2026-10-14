namespace ArturRios.Heimdall.Domain.Entities;

/// <summary>
///     A single-use token issued for one person and valid until <see cref="ExpiresAt" />
///     (<see cref="EmailVerificationToken" />, <see cref="PasswordResetToken" />). Lets the
///     administrator-email cooldown (FR-EV-07, FR-PR-06) read either kind the same way.
/// </summary>
public interface IPersonToken
{
    /// <summary>Internal id of the person the token was issued for.</summary>
    long PersonId { get; }

    /// <summary>Expiration timestamp: the issue time plus the kind's lifetime.</summary>
    DateTime ExpiresAt { get; }
}
