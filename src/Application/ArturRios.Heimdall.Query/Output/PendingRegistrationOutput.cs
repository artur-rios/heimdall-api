using ArturRios.Mediator.Query;

namespace ArturRios.Heimdall.Query.Output;

/// <summary>
///     One self-registration awaiting confirmation (UC-49, FR-SR-11): what a scope's owners need to
///     recognise it and decide whether to discard it.
/// </summary>
/// <remarks>
///     Deliberately without the password hash, the salt and the token hash. The name and address are
///     what the owners would see in the scope's user list once the link is followed, and a taken
///     address never produces a pending row, so the listing reveals nothing about existing accounts.
/// </remarks>
public class PendingRegistrationOutput : QueryOutput
{
    /// <summary>The registration's public id — what UC-50 discards it by.</summary>
    public Guid Id { get; init; }

    /// <summary>The name the registrant gave.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>The address awaiting confirmation.</summary>
    public string Email { get; init; } = string.Empty;

    /// <summary>When the registration was stored.</summary>
    public DateTime CreatedAt { get; init; }

    /// <summary>When its link stops working.</summary>
    public DateTime ExpiresAt { get; init; }

    /// <summary>Wrong passwords the registrant may still try before the registration is discarded (FR-SR-09); never below 0.</summary>
    public int AttemptsLeft { get; init; }

    /// <summary>
    ///     Whether the link has already expired. Such a row stays until the scheduled purge removes it,
    ///     so it is listed and may be discarded like any other.
    /// </summary>
    public bool Expired { get; init; }
}
