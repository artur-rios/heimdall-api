using ArturRios.Mediator.Query;

namespace ArturRios.Heimdall.Query.Output;

/// <summary>
///     One open invitation to co-own a scope, as the scope's owners and System Admins see it (UC-58): who is
///     invited, who invited them, and for how long it stands.
/// </summary>
public class ScopeOwnerInvitationOutput : QueryOutput
{
    /// <summary>The invitation's public id — what UC-58 withdraws it by.</summary>
    public Guid Id { get; init; }

    /// <summary>The invited administrator's public id.</summary>
    public Guid InviteeId { get; init; }

    public string InviteeName { get; init; } = string.Empty;

    public string InviteeEmail { get; init; } = string.Empty;

    /// <summary>Who invited them; <c>null</c> once that person was anonymised, removed or restricted.</summary>
    public string? InvitedByName { get; init; }

    public DateTime CreatedAt { get; set; }

    /// <summary>When it stops being acceptable.</summary>
    public DateTime ExpiresAt { get; init; }
}
