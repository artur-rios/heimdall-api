using ArturRios.Mediator.Command;

namespace ArturRios.Heimdall.Command.Output;

/// <summary>
///     Result of <see cref="Input.HardDeleteGoogleUserCommand" /> (UC-29). Reports the removed Google
///     User's <c>PublicId</c> alone: a Google User is a leaf in the data model, so — unlike
///     <see cref="HardDeleteScopeCommandOutput" /> and <see cref="HardDeletePersonCommandOutput" /> —
///     there is no dependent total to report. Internal Ids never leave the data layer.
/// </summary>
public class HardDeleteGoogleUserCommandOutput : CommandOutput
{
    /// <summary>Public identifier of the hard-deleted Google User.</summary>
    public Guid Id { get; set; }
}
