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
    /// <summary>
    ///     The confirmation-guess budget (FR-SR-09): the login lockout threshold, so a leaked link admits
    ///     no more guesses at the sign-up password than the account itself would. Spent by
    ///     <c>PendingRegistrationConfirmer</c>; what is left is reported by the UC-49 listing.
    /// </summary>
    public const int MaxConfirmationGuesses = 10;

    /// <summary>
    ///     The live registrations one address may have in one scope at a time (FR-SR-13). Each is a
    ///     confirmation mail to the address, so this bounds the mail registering can send it: a request
    ///     beyond it creates and mails nothing, and answers exactly as any other does (AF-47b).
    /// </summary>
    public const int MaxLivePerAddress = 3;

    /// <summary>
    ///     The administrative reference (UC-49, UC-50): how a scope's owners name this registration to
    ///     discard it. Random, so it discloses nothing about the registrant or how many registrations
    ///     exist. The registrant never sees it; their reference is still the mailed token.
    /// </summary>
    public Guid PublicId { get; init; } = Guid.NewGuid();

    /// <summary>Foreign key to the scope registered into (internal Id).</summary>
    public long ScopeId { get; init; }

    [MaxLength(200)]
    public string Name { get; init; } = string.Empty;

    public string Email { get; init; } = string.Empty;

    /// <summary>The password hash computed on the request path; the plaintext was never stored.</summary>
    public byte[] PasswordHash { get; init; } = [];

    public byte[] Salt { get; init; } = [];

    /// <summary>SHA-256 of the mailed token, as lowercase hex, through <c>SingleUseTokenHash</c>.</summary>
    [MaxLength(64)]
    public string TokenHash { get; init; } = string.Empty;

    public DateTime ExpiresAt { get; init; }

    public DateTime CreatedAt { get; init; }

    /// <summary>
    ///     Wrong confirmation passwords presented with this registration's token. The row is discarded when
    ///     the count reaches the login lockout threshold, so a leaked link cannot be used to guess the
    ///     sign-up password without limit.
    /// </summary>
    public int FailedAttempts { get; set; }

    public Scope Scope { get; init; } = null!;
}
