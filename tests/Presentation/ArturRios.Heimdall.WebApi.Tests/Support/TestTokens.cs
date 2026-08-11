using ArturRios.Heimdall.WebApi.Security;
using ArturRios.Jwt;

namespace ArturRios.Heimdall.WebApi.Tests.Support;

/// <summary>
///     Mints the application's own HMAC JWT directly for functional tests that are not themselves
///     about logging in. Tokens are built by the production <see cref="IdentityUserMapper" />, so a
///     change to the claim vocabulary can never leave the suite passing against a stale token
///     format, and signed with the same secret/issuer/audience the host under test validates against
///     (published into the environment by <see cref="PostgresFixture" />).
/// </summary>
/// <remarks>
///     Authentication runs in <c>ClaimsOnly</c> mode, so the person a token names need not exist in
///     the database — which keeps tests that only exercise a role gate from having to seed one.
///     Tests whose behaviour depends on <em>who</em> the caller is must pass that person's
///     <c>PublicId</c> explicitly.
/// </remarks>
public static class TestTokens
{
    private const string SecretVariable = "HEIMDALL_AUTH_TOKEN_SECRET";
    private const string IssuerVariable = "HEIMDALL_AUTH_TOKEN_ISSUER";
    private const string AudienceVariable = "HEIMDALL_AUTH_TOKEN_AUDIENCE";

    /// <summary>
    ///     Builds a bearer token for an unspecified person with the given role value (see
    ///     <c>Roles</c>), for tests that only care that the caller holds the role.
    /// </summary>
    public static string ForRole(int role) => For(Guid.NewGuid(), role);

    /// <summary>Builds a bearer token for a specific person's <c>PublicId</c> and role value.</summary>
    public static string For(
        Guid personId, int role, Guid? scopeId = null, params Guid[] ownedScopeIds) =>
        Create(new IdentityUser(personId, role, scopeId, ownedScopeIds));

    /// <summary>
    ///     Builds a UC-38 challenge-shaped bearer token — <c>MfaPending</c> set, no scope claims —
    ///     for tests proving <see cref="MfaPendingGuardFilter" /> rejects one everywhere except
    ///     <c>POST /api/auth/2fa/verify</c> (FR-2F-10).
    /// </summary>
    public static string ForMfaPending(Guid personId, int role) =>
        Create(new IdentityUser(personId, role) { MfaPending = true });

    private static string Create(IdentityUser user)
    {
        var configuration = new JwtConfiguration(
            3600,
            Environment.GetEnvironmentVariable(IssuerVariable) ?? string.Empty,
            Environment.GetEnvironmentVariable(AudienceVariable) ?? string.Empty,
            Environment.GetEnvironmentVariable(SecretVariable) ?? string.Empty,
            new IdentityUserMapper().ToClaims(user));

        return new JwtHandler().CreateToken(configuration);
    }
}
