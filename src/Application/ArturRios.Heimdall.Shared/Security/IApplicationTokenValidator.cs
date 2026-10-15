namespace ArturRios.Heimdall.Shared.Security;

/// <summary>What a valid application token says, before the database is consulted (UC-57).</summary>
/// <param name="ApplicationId">The application's <c>PublicId</c>.</param>
/// <param name="ScopeId">Its scope's <c>PublicId</c>.</param>
/// <param name="CredentialId">The issuing credential's <c>PublicId</c>.</param>
/// <param name="ExpiresAt">When the token expires (UTC).</param>
public sealed record ApplicationTokenPrincipal(Guid ApplicationId, Guid ScopeId, Guid CredentialId, DateTime ExpiresAt);

/// <summary>
///     Validates an application token offline — signature against the current and previous keys,
///     issuer, the application audience, lifetime with no clock skew, and the application kind
///     (UC-57, TH-35). Whether the application, scope and credential are still live is the caller's
///     question, answered against the database.
/// </summary>
public interface IApplicationTokenValidator
{
    /// <param name="token">The bearer token presented to <c>GET /api/applications/self</c>.</param>
    /// <returns>The principal, or <see langword="null" /> when the token is not a valid application token.</returns>
    Task<ApplicationTokenPrincipal?> ValidateAsync(string? token);
}
