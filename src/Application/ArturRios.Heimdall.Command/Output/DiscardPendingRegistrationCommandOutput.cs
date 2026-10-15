using ArturRios.Mediator.Command;

namespace ArturRios.Heimdall.Command.Output;

/// <summary>
///     Result of <see cref="Input.DiscardPendingRegistrationCommand" /> (UC-50). <see cref="Id" /> is the
///     discarded registration's public id, which is also what the audit entry records as its target.
/// </summary>
public class DiscardPendingRegistrationCommandOutput : CommandOutput
{
    /// <summary>Public id of the discarded registration.</summary>
    public Guid Id { get; set; }
}
