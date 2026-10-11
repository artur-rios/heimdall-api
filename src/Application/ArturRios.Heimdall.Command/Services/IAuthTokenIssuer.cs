namespace ArturRios.Heimdall.Command.Services;

/// <summary>
///     Who an authentication token is being issued for (UC-11 step 6, FR-AU-04). Every identifier is
///     a <c>PublicId</c>: internal <c>bigint</c> Ids never reach a token (NFR-15).
/// </summary>
/// <param name="PersonId">The authenticated person's <c>PublicId</c>.</param>
/// <param name="DisplayName">Their display name, included for downstream profile provisioning.</param>
/// <param name="RoleId">Their role value (see <c>Roles</c>).</param>
/// <param name="ScopeId">
///     The <c>PublicId</c> of the scope a <c>User</c> belongs to; <c>null</c> for a
///     <c>ScopeAdmin</c> or <c>SystemAdmin</c>.
/// </param>
/// <param name="OwnedScopeIds">
///     The <c>PublicId</c>s of the non-deleted scopes a <c>ScopeAdmin</c> owns; empty otherwise.
/// </param>
public record AuthTokenSubject(
    Guid PersonId,
    string DisplayName,
    int RoleId,
    Guid? ScopeId,
    IReadOnlyCollection<Guid> OwnedScopeIds);

/// <summary>An issued authentication token and the moment it stops being valid.</summary>
/// <param name="Token">The signed token.</param>
/// <param name="ExpiresAt">The token's expiry, in UTC.</param>
public record AuthToken(string Token, DateTime ExpiresAt);

/// <summary>
///     Issues the authentication token UC-11 returns on a successful login (FR-AU-03). The signing
///     scheme and the claim vocabulary belong to the presentation layer, so the application layer
///     only says who the token is for. The call is asynchronous because the presentation's
///     implementation reads the acting scope's flagged permissions from the database before it
///     builds the claims (UC-31…UC-35, FR-SP).
/// </summary>
public interface IAuthTokenIssuer
{
    /// <param name="subject">The person the token represents, and the scopes it should claim.</param>
    /// <returns>The signed token and its expiry.</returns>
    Task<AuthToken> IssueAsync(AuthTokenSubject subject);

    /// <summary>
    ///     Issues the token a restricted identity receives when its credential checks out (NFR-24,
    ///     UC-45): it names the subject and their role and nothing else — no scope, no owned scopes,
    ///     no scope permissions — and is marked as admitting only the endpoints through which a
    ///     subject exercises a right over their own identity (UC-41, UC-42, UC-44, UC-45).
    /// </summary>
    /// <param name="subjectId">The restricted identity's <c>PublicId</c>.</param>
    /// <param name="roleId">Its role value (see <c>Roles</c>); a Google User's is always <c>User</c>.</param>
    /// <returns>The signed token and its expiry.</returns>
    Task<AuthToken> IssueSubjectRightsOnlyAsync(Guid subjectId, int roleId);
}
