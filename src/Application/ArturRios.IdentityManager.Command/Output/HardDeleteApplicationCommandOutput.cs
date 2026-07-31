using ArturRios.Mediator.Command;

namespace ArturRios.IdentityManager.Command.Output;

/// <summary>
///     Result of <see cref="Input.HardDeleteApplicationCommand" /> (UC-20). Reports the removed
///     application's <c>PublicId</c> alone: an application is a leaf in the data model, so — unlike
///     <see cref="HardDeleteScopeCommandOutput" /> and <see cref="HardDeletePersonCommandOutput" /> —
///     there is no dependent total to report. Internal Ids never leave the data layer.
/// </summary>
public class HardDeleteApplicationCommandOutput : CommandOutput
{
    /// <summary>Public identifier of the hard-deleted application.</summary>
    public Guid Id { get; set; }
}
