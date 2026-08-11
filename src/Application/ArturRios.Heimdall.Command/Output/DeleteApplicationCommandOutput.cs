using ArturRios.Mediator.Command;

namespace ArturRios.Heimdall.Command.Output;

/// <summary>
///     Result of <see cref="Input.DeleteApplicationCommand" /> (UC-19). Reports the application's
///     <c>PublicId</c> and whether the request was the idempotent no-op of AF-19b. Internal Ids never
///     leave the data layer.
/// </summary>
public class DeleteApplicationCommandOutput : CommandOutput
{
    /// <summary>Public identifier of the deleted application.</summary>
    public Guid Id { get; set; }

    /// <summary>
    ///     <c>true</c> when the application was already logically deleted and nothing was written
    ///     (AF-19b); <c>false</c> when this request performed the deletion. AF-19b answers with the
    ///     same status and message as the main flow, so this flag is what tells them apart.
    /// </summary>
    public bool AlreadyDeleted { get; set; }
}
