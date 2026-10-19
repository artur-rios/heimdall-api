using ArturRios.Mediator.Query;

namespace ArturRios.Heimdall.Query.Output;

/// <summary>
///     One open invitation to co-own a scope, as the invited administrator sees it (UC-59): which scope, who
///     invited them, and until when they can accept.
/// </summary>
public class MyScopeInvitationOutput : QueryOutput
{
    /// <summary>The invitation's public id — what UC-60 and UC-61 answer it by.</summary>
    public Guid Id { get; set; }

    public Guid ScopeId { get; set; }

    public string ScopeName { get; set; } = string.Empty;

    /// <summary>Who invited them; <c>null</c> once that person was anonymised, removed or restricted.</summary>
    public string? InvitedByName { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime ExpiresAt { get; set; }
}
