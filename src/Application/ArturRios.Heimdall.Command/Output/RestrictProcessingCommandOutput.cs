using ArturRios.Mediator.Command;

namespace ArturRios.Heimdall.Command.Output;

/// <summary>Result of <see cref="Input.RestrictProcessingCommand" /> (UC-44).</summary>
public class RestrictProcessingCommandOutput : CommandOutput
{
    /// <summary>Public identifier of the restricted identity.</summary>
    public Guid Id { get; set; }

    /// <summary>When the restriction took effect.</summary>
    public DateTime RestrictedAt { get; set; }

    /// <summary>The Art. 18(1) ground it rests on.</summary>
    public int Ground { get; set; }
}
