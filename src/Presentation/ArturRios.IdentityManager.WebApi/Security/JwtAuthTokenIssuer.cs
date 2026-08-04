using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.IdentityManager.Command.Services;
using ArturRios.IdentityManager.Domain.Entities;
using ArturRios.Jwt;
using ArturRios.Util.WebApi.Security.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.IdentityManager.WebApi.Security;

/// <summary>
///     Issues UC-11's authentication token as the application's own HMAC-signed JWT (FR-AU-03),
///     using the same <see cref="IAuthenticatedUserMapper" /> the middleware validates incoming
///     tokens with — so a token this class issues is one the API can always read back.
/// </summary>
/// <remarks>
///     Expiry comes from the registered <see cref="JwtConfiguration" />, which reads it from the
///     environment (NFR-03), and is reported to the caller so a client need not decode the token to
///     know when to re-authenticate. At issuance the acting scope's permissions whose
///     <see cref="ScopePermission.IncludeAsJwtClaim" /> flag is set are read from the database
///     (UC-31…UC-35, FR-SP) and folded into the token, so a downstream caller can authorize on them.
///     A User claims the permissions of the scope they belong to; a Scope Admin the union over the
///     scopes they own; a System Admin none, since they belong to no scope. Logically deleted scopes
///     and permissions are excluded, as a deleted resource is never claimed.
/// </remarks>
public class JwtAuthTokenIssuer(
    JwtConfiguration configuration,
    JwtHandler jwtHandler,
    IAuthenticatedUserMapper mapper,
    IAsyncReadOnlyRepository<ScopePermission> permissionReader) : IAuthTokenIssuer
{
    public async Task<AuthToken> IssueAsync(AuthTokenSubject subject)
    {
        var permissionClaims = await LoadScopePermissionClaimsAsync(subject);

        // The issued token carries this login's claims; everything else — secret, issuer, audience,
        // lifetime — comes from the configuration the validator also uses.
        var user = new IdentityUser(
            subject.PersonId, subject.RoleId, subject.ScopeId, subject.OwnedScopeIds)
        {
            ScopePermissionClaims = permissionClaims
        };

        var tokenConfiguration = configuration with { Claims = mapper.ToClaims(user) };

        return new AuthToken(
            jwtHandler.CreateToken(tokenConfiguration),
            DateTime.UtcNow.AddSeconds(configuration.ExpirationInSeconds));
    }

    /// <summary>
    ///     The distinct names of the non-deleted permissions flagged <see cref="ScopePermission.IncludeAsJwtClaim" />
    ///     across the scopes this subject acts within — <see cref="AuthTokenSubject.ScopeId" /> for a
    ///     User and <see cref="AuthTokenSubject.OwnedScopeIds" /> for a Scope Admin. Empty for a
    ///     System Admin, who carries no scope.
    /// </summary>
    private async Task<IReadOnlyCollection<string>> LoadScopePermissionClaimsAsync(AuthTokenSubject subject)
    {
        var scopePublicIds = subject.OwnedScopeIds.ToList();

        if (subject.ScopeId is not null)
        {
            scopePublicIds.Add(subject.ScopeId.Value);
        }

        if (scopePublicIds.Count == 0)
        {
            return [];
        }

        return await permissionReader.Query()
            .Where(permission => scopePublicIds.Contains(permission.Scope.PublicId)
                                 && !permission.Scope.IsDeleted
                                 && !permission.IsDeleted
                                 && permission.IncludeAsJwtClaim)
            .Select(permission => permission.Name)
            .Distinct()
            .ToListAsync();
    }
}
