using ArturRios.Mediator.Command;

namespace ArturRios.Heimdall.Command.Output;

/// <summary>
///     Result of <see cref="Input.RegisterCommand" /> (UC-47). Deliberately empty, for the reason
///     <see cref="PasswordRecoveryCommandOutput" /> is: any field describing what happened would say
///     whether the address was already taken.
/// </summary>
public class RegisterCommandOutput : CommandOutput;
