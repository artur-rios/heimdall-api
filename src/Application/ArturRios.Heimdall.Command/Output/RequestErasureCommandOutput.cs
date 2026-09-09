using ArturRios.Mediator.Command;

namespace ArturRios.Heimdall.Command.Output;

/// <summary>
///     Result of <see cref="Input.RequestErasureCommand" /> (UC-42): what the subject is owed and
///     by when.
/// </summary>
public class RequestErasureCommandOutput : CommandOutput
{
    /// <summary>Public identifier of the identity the erasure was requested for.</summary>
    public Guid Id { get; set; }

    /// <summary>When the request was recorded.</summary>
    public DateTime RequestedAt { get; set; }

    /// <summary>
    ///     The deadline the erasure must not outlive. Returned to the subject because GDPR Art.
    ///     12(3) is an obligation owed to them, and one they cannot hold anyone to without knowing
    ///     it.
    /// </summary>
    public DateTime DueAt { get; set; }

    /// <summary>
    ///     Whether something prevents the erasure from proceeding yet. The request is recorded and
    ///     the deadline runs either way.
    /// </summary>
    public bool Blocked { get; set; }

    /// <summary>Why it is blocked, when it is; <c>null</c> otherwise.</summary>
    public string? BlockedReason { get; set; }
}
