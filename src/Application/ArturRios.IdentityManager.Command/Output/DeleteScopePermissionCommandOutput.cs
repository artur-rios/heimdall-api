using ArturRios.Mediator.Command;

namespace ArturRios.IdentityManager.Command.Output;

/// <summary>
///     Result of <see cref="Input.DeleteScopePermissionCommand" /> (UC-34). Reports the permission's
///     <c>PublicId</c> and whether the request was the idempotent no-op of AF-34b. Internal Ids
///     never leave the data layer.
/// </summary>
public class DeleteScopePermissionCommandOutput : CommandOutput
{
    /// <summary>Public identifier of the deleted permission.</summary>
    public Guid Id { get; set; }

    /// <summary>
    ///     <c>true</c> when the permission was already logically deleted and nothing was written
    ///     (AF-34b); <c>false</c> when this request performed the deletion. AF-34b answers with the
    ///     same status and message as the main flow, so this flag is what tells them apart.
    /// </summary>
    public bool AlreadyDeleted { get; set; }
}