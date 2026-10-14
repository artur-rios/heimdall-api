using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Shared.Services;

namespace ArturRios.Heimdall.Command.Services;

/// <summary>
///     Who may have an account email sent to whom (UC-51, UC-52, AF-51b/AF-52b): a System Admin to any
///     person; a Scope Admin only to a <c>User</c> of a scope they own. Shared by both use cases so they
///     cannot drift apart.
/// </summary>
public static class PersonEmailAuthorization
{
    /// <summary>
    ///     Whether the actor may act on <paramref name="person" />. A Scope Admin aiming at anyone but a
    ///     <c>User</c> is refused without a database read; for a <c>User</c>, ownership of
    ///     <c>Person.ScopeId</c> is asked of <paramref name="scopeOwnership" />.
    /// </summary>
    public static async Task<bool> ActorMayEmailAsync(
        IScopeOwnershipChecker scopeOwnership, int actingRole, Guid actingPersonId, Person person)
    {
        if (actingRole == (int)Roles.SystemAdmin)
        {
            return true;
        }

        if (actingRole != (int)Roles.ScopeAdmin
            || person.RoleId != (long)Roles.User
            || person.ScopeId is not { } scopeId)
        {
            return false;
        }

        return await scopeOwnership.ActorMayManageScopeAsync(actingRole, actingPersonId, scopeId);
    }
}
