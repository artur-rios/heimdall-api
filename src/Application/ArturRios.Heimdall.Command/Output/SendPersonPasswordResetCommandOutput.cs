using ArturRios.Mediator.Command;

namespace ArturRios.Heimdall.Command.Output;

/// <summary>
///     Result of <see cref="Input.SendPersonPasswordResetCommand" /> (UC-52). Only the person's public
///     id — the audit target. Never the token, which would let the caller set the person's password.
/// </summary>
public class SendPersonPasswordResetCommandOutput : CommandOutput
{
    /// <summary>Public id of the person emailed.</summary>
    public Guid Id { get; set; }
}
