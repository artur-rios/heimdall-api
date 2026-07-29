using ArturRios.Mediator.Command;

namespace ArturRios.IdentityManager.Command.Input;

/// <summary>
///     Intent to permanently (hard) delete a scope (UC-05). The scope is addressed by its
///     <c>PublicId</c> (GUID), bound from the route. Removing the scope also permanently removes its
///     Users, Google Users, applications, and its <c>SCOPE_OWNER</c>/<c>SCOPE_USER</c> join rows; the
///     owner person records are left intact.
/// </summary>
public class HardDeleteScopeCommand : BaseCommand
{
    /// <summary>Public identifier of the scope to hard-delete (bound from the route).</summary>
    public Guid Id { get; set; }
}
