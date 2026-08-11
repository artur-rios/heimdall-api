using ArturRios.Mediator.Command;

namespace ArturRios.Heimdall.Command.Output;

/// <summary>
///     Result of <see cref="Input.ResetPasswordCommand" /> (UC-13). Empty by design: the caller has
///     proved only that they hold a reset token, which is not authentication, so the response says
///     that the password changed and nothing about whose it was. A token is obtained at
///     <c>/api/auth/login</c>, as before.
/// </summary>
public class ResetPasswordCommandOutput : CommandOutput;
