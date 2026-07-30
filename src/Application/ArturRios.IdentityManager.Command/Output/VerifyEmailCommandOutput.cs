using ArturRios.Mediator.Command;

namespace ArturRios.IdentityManager.Command.Output;

/// <summary>
///     Result of <see cref="Input.VerifyEmailCommand" /> (UC-14). Empty by design: the caller has
///     proved only that they hold a verification token, which is not authentication, so the response
///     says that the address was verified and nothing about whose it was. A token is obtained at
///     <c>/api/auth/login</c>, as before.
/// </summary>
public class VerifyEmailCommandOutput : CommandOutput;
