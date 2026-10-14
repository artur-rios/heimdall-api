using ArturRios.Mediator.Command;

namespace ArturRios.Heimdall.Command.Output;

/// <summary>
///     Result of <see cref="Input.HardDeleteApplicationCommand" /> (UC-20). Reports the removed
///     application's <c>PublicId</c> alone. Its credentials (UC-53) are removed with it by cascade,
///     but — unlike <see cref="HardDeleteScopeCommandOutput" /> and
///     <see cref="HardDeletePersonCommandOutput" /> — no dependent total is reported for them.
///     Internal Ids never leave the data layer.
/// </summary>
public class HardDeleteApplicationCommandOutput : CommandOutput
{
    /// <summary>Public identifier of the hard-deleted application.</summary>
    public Guid Id { get; set; }
}
