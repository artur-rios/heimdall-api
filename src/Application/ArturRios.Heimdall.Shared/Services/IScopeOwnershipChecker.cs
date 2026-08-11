namespace ArturRios.Heimdall.Shared.Services;

/// <summary>
///     Decides whether an acting caller is authorized to manage or read a given scope (UC-06 AF-06e,
///     UC-07 AF-07b, and any other scope-scoped authorization): a System Admin always may; any other
///     actor must own the scope (a <c>SCOPE_OWNER</c> row links their person id to it).
/// </summary>
public interface IScopeOwnershipChecker
{
    /// <param name="actingRole">The acting caller's role value (see <c>Roles</c>).</param>
    /// <param name="actingPersonId">The acting caller's person <c>PublicId</c>.</param>
    /// <param name="scopeId">The target scope's internal id.</param>
    /// <returns><c>true</c> when the actor is a System Admin or owns the scope; otherwise <c>false</c>.</returns>
    Task<bool> ActorMayManageScopeAsync(int actingRole, Guid actingPersonId, long scopeId);
}
