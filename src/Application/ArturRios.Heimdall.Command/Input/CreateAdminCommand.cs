using ArturRios.Mediator.Command;

namespace ArturRios.Heimdall.Command.Input;

/// <summary>
///     Intent to create a <c>ScopeAdmin</c> or <c>SystemAdmin</c> person without any scope
///     association (UC-06 path b). <see cref="Role" /> is the <c>Roles</c> enum value and must be
///     <c>SystemAdmin</c> or <c>ScopeAdmin</c>.
/// </summary>
public class CreateAdminCommand : BaseCommand
{
    public string Name { get; set; } = string.Empty;

    public string Email { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    public int Role { get; set; }
}
