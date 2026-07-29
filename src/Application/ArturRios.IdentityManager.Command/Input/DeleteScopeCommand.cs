using ArturRios.Mediator.Command;

namespace ArturRios.IdentityManager.Command.Input;

/// <summary>
///     Intent to logically delete a scope (UC-04). The scope is addressed by its <c>PublicId</c>
///     (GUID), bound from the route. Setting the scope's <c>IsDeleted</c> flag cascades to its Users,
///     Google Users, and applications.
/// </summary>
public class DeleteScopeCommand : BaseCommand
{
    /// <summary>Public identifier of the scope to delete (bound from the route).</summary>
    public Guid Id { get; set; }
}
