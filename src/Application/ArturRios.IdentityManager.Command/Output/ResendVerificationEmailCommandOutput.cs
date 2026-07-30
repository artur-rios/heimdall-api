using ArturRios.Mediator.Command;

namespace ArturRios.IdentityManager.Command.Output;

/// <summary>
///     Result of <see cref="Input.ResendVerificationEmailCommand" /> (UC-15). Empty by design: the
///     token goes to the mailbox and nowhere else. Returning it here would let a caller verify an
///     address without ever reading the mail sent to it, which is the one thing verification exists to
///     prove.
/// </summary>
public class ResendVerificationEmailCommandOutput : CommandOutput;
