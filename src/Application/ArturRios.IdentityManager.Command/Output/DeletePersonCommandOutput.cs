using ArturRios.Mediator.Command;

namespace ArturRios.IdentityManager.Command.Output;

/// <summary>
///     Result of <see cref="Input.DeletePersonCommand" /> (UC-09). Reports the person's
///     <c>PublicId</c> and whether the request was the idempotent no-op of AF-09b. Internal Ids never
///     leave the data layer.
/// </summary>
public class DeletePersonCommandOutput : CommandOutput
{
    /// <summary>Public identifier of the deleted person.</summary>
    public Guid Id { get; set; }

    /// <summary>
    ///     <c>true</c> when the person was already logically deleted and nothing was written
    ///     (AF-09b); <c>false</c> when this request performed the deletion.
    /// </summary>
    public bool AlreadyDeleted { get; set; }
}
