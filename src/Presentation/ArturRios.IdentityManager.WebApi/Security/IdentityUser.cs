using ArturRios.Util.WebApi.Security.Interfaces;

namespace ArturRios.IdentityManager.WebApi.Security;

/// <summary>
///     The caller identity this API carries on an authenticated request, rebuilt from the token's
///     claims by <see cref="IdentityUserMapper" /> (UC-11, FR-AU-04). Every identifier is a
///     <c>PublicId</c>: internal <c>bigint</c> Ids never reach a token or the request pipeline
///     (NFR-15).
/// </summary>
/// <param name="Id">The person's <c>PublicId</c>.</param>
/// <param name="RoleId">The person's role value (see <c>Roles</c>).</param>
/// <param name="ScopeId">
///     The <c>PublicId</c> of the scope a <c>User</c> belongs to; <c>null</c> for a
///     <c>ScopeAdmin</c> or <c>SystemAdmin</c>.
/// </param>
/// <param name="OwnedScopeIds">
///     The <c>PublicId</c>s of the scopes a <c>ScopeAdmin</c> owns; empty for a <c>User</c> or
///     <c>SystemAdmin</c>.
/// </param>
public sealed record IdentityUser(
    Guid Id,
    int RoleId,
    Guid? ScopeId,
    IReadOnlyCollection<Guid> OwnedScopeIds) : IAuthenticatedUser
{
    /// <summary>
    ///     The names of the acting scope's permissions whose <c>IncludeAsJwtClaim</c> flag is set,
    ///     folded into the token at login so a downstream caller can authorize on them (UC-31…UC-35).
    ///     Empty for a <c>SystemAdmin</c> (no scope) and when a scope has no flagged permissions.
    /// </summary>
    public IReadOnlyCollection<string> ScopePermissionClaims { get; init; } = [];

    /// <summary>An identity carrying no scope claim, for a <c>SystemAdmin</c>.</summary>
    public IdentityUser(Guid id, int roleId) : this(id, roleId, null, [])
    {
    }
}
