using ArturRios.Mediator.Query;

namespace ArturRios.Heimdall.Query.Output;

/// <summary>
///     One outstanding erasure request (UC-43). Deliberately carries no name and no email address:
///     the subject has asked to be erased, and a queue an administrator works from is the last place
///     to widen who sees their details. The public identifier is enough to act on.
/// </summary>
public class ErasureRequestOutput : QueryOutput
{
    /// <summary>Public identifier of the identity to be erased.</summary>
    public Guid SubjectId { get; set; }

    /// <summary>Whether the subject is a Google User rather than a person.</summary>
    public bool IsGoogleUser { get; set; }

    /// <summary>When the subject asked.</summary>
    public DateTime RequestedAt { get; set; }

    /// <summary>The deadline the erasure must not outlive.</summary>
    public DateTime DueAt { get; set; }

    /// <summary>Whether that deadline has already passed.</summary>
    public bool Overdue { get; set; }

    /// <summary>
    ///     Why the erasure cannot proceed, or <c>null</c> if nothing blocks it. A request with no
    ///     reason needs no action: the scheduled pass will carry it out on the deadline.
    /// </summary>
    public string? BlockedReason { get; set; }
}
