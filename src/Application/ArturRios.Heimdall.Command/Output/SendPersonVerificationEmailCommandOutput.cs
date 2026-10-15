using ArturRios.Mediator.Command;

namespace ArturRios.Heimdall.Command.Output;

/// <summary>
///     Result of <see cref="Input.SendPersonVerificationEmailCommand" /> (UC-51). Only the person's
///     public id — the audit target. Never the token: it goes to the mailbox and nowhere else.
/// </summary>
public class SendPersonVerificationEmailCommandOutput : CommandOutput
{
    /// <summary>Public id of the person emailed.</summary>
    public Guid Id { get; set; }
}
