using ArturRios.Mediator.Command;

namespace ArturRios.Heimdall.Command.Output;

/// <summary>
///     Result of <see cref="Input.PasswordRecoveryCommand" /> (UC-12). Deliberately empty: the
///     response carries only <see cref="Shared.Messages.AuthMessages.PasswordRecoveryRequested" />,
///     since any field describing what happened — whether a person was found, when the token
///     expires, where it was sent — would answer the question AF-12a exists to leave unanswered.
/// </summary>
public class PasswordRecoveryCommandOutput : CommandOutput;
