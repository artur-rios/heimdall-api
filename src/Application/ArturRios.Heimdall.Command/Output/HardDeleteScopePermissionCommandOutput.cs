using ArturRios.Mediator.Command;

namespace ArturRios.Heimdall.Command.Output;

/// <summary>
///     Result of <see cref="Input.HardDeleteScopePermissionCommand" /> (UC-35). Reports the removed
///     permission's <c>PublicId</c> alone: a scope permission is a leaf in the data model, so there
///     is no dependent total to report. Internal Ids never leave the data layer.
/// </summary>
public class HardDeleteScopePermissionCommandOutput : CommandOutput
{
    /// <summary>Public identifier of the hard-deleted permission.</summary>
    public Guid Id { get; set; }
}