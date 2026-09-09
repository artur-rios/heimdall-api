using ArturRios.Mediator.Command;

namespace ArturRios.Heimdall.Command.Output;

/// <summary>Result of <see cref="Input.LiftProcessingRestrictionCommand" /> (UC-45).</summary>
public class LiftProcessingRestrictionCommandOutput : CommandOutput
{
    /// <summary>Public identifier of the identity whose restriction was lifted.</summary>
    public Guid Id { get; set; }

    /// <summary>When it was lifted.</summary>
    public DateTime LiftedAt { get; set; }

    /// <summary>
    ///     Whether the subject was notified first. False only where the subject lifted their own
    ///     restriction, in which case Art. 18(3) has nobody to inform.
    /// </summary>
    public bool SubjectNotified { get; set; }
}
