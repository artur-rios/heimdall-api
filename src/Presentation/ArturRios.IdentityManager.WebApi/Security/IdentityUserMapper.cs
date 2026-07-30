using ArturRios.IdentityManager.Domain.Enums;
using ArturRios.Util.WebApi.Security.Constants;
using ArturRios.Util.WebApi.Security.Interfaces;

namespace ArturRios.IdentityManager.WebApi.Security;

/// <summary>
///     Translates between <see cref="IdentityUser" /> and the claims this API's tokens carry
///     (UC-11, FR-AU-04). One class owns both directions, so the claims written when a token is
///     issued and the claims read when it is validated cannot drift apart.
/// </summary>
/// <remarks>
///     Every claim value is a <c>PublicId</c> — an internal <c>bigint</c> Id is never written to a
///     token (NFR-15). Reading is total: any claim that cannot be interpreted yields <c>null</c>
///     rather than an exception, so a malformed token is rejected as unauthenticated instead of
///     failing the request with a 500.
/// </remarks>
public class IdentityUserMapper : IAuthenticatedUserMapper
{
    /// <summary>The claim holding the scope a <c>User</c> belongs to.</summary>
    public const string ScopeIdClaim = "scopeId";

    /// <summary>The claim holding the scopes a <c>ScopeAdmin</c> owns, comma-separated.</summary>
    public const string OwnedScopeIdsClaim = "ownedScopeIds";

    private const char OwnedScopeIdsSeparator = ',';

    public Dictionary<string, string> ToClaims(IAuthenticatedUser user)
    {
        var claims = new Dictionary<string, string>
        {
            [TokenClaimKeys.Id] = user.Id.ToString(),
            [TokenClaimKeys.RoleId] = user.RoleId.ToString()
        };

        if (user is not IdentityUser identityUser)
        {
            return claims;
        }

        // FR-AU-04: a User carries their scope, a ScopeAdmin the scopes they own, a SystemAdmin
        // neither. The claims are omitted rather than emitted empty, so a token never suggests a
        // scope association the person does not have.
        if (identityUser.ScopeId is not null)
        {
            claims[ScopeIdClaim] = identityUser.ScopeId.Value.ToString();
        }

        if (identityUser.OwnedScopeIds.Count > 0)
        {
            claims[OwnedScopeIdsClaim] = string.Join(
                OwnedScopeIdsSeparator, identityUser.OwnedScopeIds);
        }

        return claims;
    }

    public IAuthenticatedUser? FromClaims(IReadOnlyDictionary<string, string> claims)
    {
        if (!TryReadGuid(claims, TokenClaimKeys.Id, out var id))
        {
            return null;
        }

        if (!claims.TryGetValue(TokenClaimKeys.RoleId, out var rawRole) ||
            !int.TryParse(rawRole, out var roleId) ||
            !Enum.IsDefined(typeof(Roles), roleId))
        {
            return null;
        }

        var scopeId = TryReadGuid(claims, ScopeIdClaim, out var parsedScopeId)
            ? parsedScopeId
            : (Guid?)null;

        return new IdentityUser(id, roleId, scopeId, ReadOwnedScopeIds(claims));
    }

    private static IReadOnlyCollection<Guid> ReadOwnedScopeIds(IReadOnlyDictionary<string, string> claims)
    {
        if (!claims.TryGetValue(OwnedScopeIdsClaim, out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        // An unparseable entry is dropped rather than failing the whole token: the remaining owned
        // scopes are still a true statement about the caller, and authorization is a per-scope
        // check anyway.
        return raw
            .Split(OwnedScopeIdsSeparator, StringSplitOptions.RemoveEmptyEntries |
                                           StringSplitOptions.TrimEntries)
            .Select(entry => Guid.TryParse(entry, out var scopeId) ? scopeId : (Guid?)null)
            .Where(scopeId => scopeId is not null)
            .Select(scopeId => scopeId!.Value)
            .ToList();
    }

    private static bool TryReadGuid(
        IReadOnlyDictionary<string, string> claims, string key, out Guid value)
    {
        value = Guid.Empty;

        return claims.TryGetValue(key, out var raw) && Guid.TryParse(raw, out value);
    }
}
