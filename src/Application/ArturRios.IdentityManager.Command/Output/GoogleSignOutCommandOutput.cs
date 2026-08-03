using ArturRios.Mediator.Command;

namespace ArturRios.IdentityManager.Command.Output;

/// <summary>
///     Result of <see cref="Input.GoogleSignOutCommand" /> (UC-26). Empty by design: under this
///     project's stateless token strategy the sign-out has nothing to report back — the token the
///     caller sent is the token they discard, and no replacement is issued.
/// </summary>
public class GoogleSignOutCommandOutput : CommandOutput;
