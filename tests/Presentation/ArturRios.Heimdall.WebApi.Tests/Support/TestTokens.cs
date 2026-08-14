using ArturRios.Heimdall.Domain.Enums;
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
///     Authentication runs in <c>ClaimsOnly</c> mode, so no database read happens per request — but
///     <see cref="ActorLivenessFilter" /> does resolve the caller, so the person a token names has to
///     exist and be live. <see cref="ForRole" /> therefore names one of
///     <see cref="PostgresFixture.StandInPersonIds" />, which the fixture seeds, and which keeps
///     tests that only exercise a role gate from having to seed anyone themselves. Tests whose
///     behaviour depends on <em>who</em> the caller is must seed that person and pass their
///     <c>PublicId</c> to <see cref="For" />.
/// </remarks>
public static class TestTokens
{
    private const string SecretVariable = "HEIMDALL_AUTH_TOKEN_SECRET";
    private const string IssuerVariable = "HEIMDALL_AUTH_TOKEN_ISSUER";
    private const string AudienceVariable = "HEIMDALL_AUTH_TOKEN_AUDIENCE";

    /// <summary>
    ///     Builds a bearer token for the stand-in person of the given role value (see <c>Roles</c>),
    ///     for tests that only care that the caller holds the role. The stand-in owns no scope and
    ///     belongs to none, so it carries exactly the authority the role alone confers.
    /// </summary>
    public static string ForRole(int role) => For(PostgresFixture.StandInPersonIds[(Roles)role], role);

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
