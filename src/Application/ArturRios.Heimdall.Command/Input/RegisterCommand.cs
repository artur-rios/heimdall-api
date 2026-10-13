using ArturRios.Mediator.Command;

namespace ArturRios.Heimdall.Command.Input;

/// <summary>
///     Intent to register a password <c>User</c> in a scope without an administrator (UC-47,
///     FR-SR-03). Anonymous: nothing here is bound from a token. <see cref="ScopeId" /> is nullable
///     so a body that omits it is refused by the validator instead of binding <c>Guid.Empty</c>.
/// </summary>
public class RegisterCommand : BaseCommand
{
    public string Name { get; set; } = string.Empty;

    public string Email { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    public Guid? ScopeId { get; set; }
}
