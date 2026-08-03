using ArturRios.Mediator.Command;

namespace ArturRios.IdentityManager.Command.Output;

/// <summary>
///     Result of <see cref="Input.DeleteGoogleUserCommand" /> (UC-28). Reports the Google User's
///     <c>PublicId</c> and whether the request was the idempotent no-op of AF-28b. Internal Ids never
///     leave the data layer.
/// </summary>
public class DeleteGoogleUserCommandOutput : CommandOutput
{
    /// <summary>Public identifier of the deleted Google User.</summary>
    public Guid Id { get; set; }

    /// <summary>
    ///     <c>true</c> when the Google User was already logically deleted and nothing was written
    ///     (AF-28b); <c>false</c> when this request performed the deletion. AF-28b answers with the
    ///     same status and message as the main flow — the specification requires it to — so this flag
    ///     is what tells them apart.
    /// </summary>
    public bool AlreadyDeleted { get; set; }
}
