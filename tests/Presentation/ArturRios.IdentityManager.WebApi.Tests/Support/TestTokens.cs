using ArturRios.Jwt;
using ArturRios.Util.WebApi.Security.Extensions;
using ArturRios.Util.WebApi.Security.Records;

namespace ArturRios.IdentityManager.WebApi.Tests.Support;

/// <summary>
///     Mints the application's own HMAC JWT directly for functional tests. UC-11 (Login) is not yet
///     implemented, so there is no auth route to exchange credentials at; tests craft a token with the
///     required <c>id</c>/<c>role</c> claims, signed with the same secret/issuer/audience the host
///     under test validates against (published into the environment by <see cref="PostgresFixture" />).
/// </summary>
public static class TestTokens
{
    private const string SecretVariable = "IDENTITY_MANAGER_AUTH_TOKEN_SECRET";
    private const string IssuerVariable = "IDENTITY_MANAGER_AUTH_TOKEN_ISSUER";
    private const string AudienceVariable = "IDENTITY_MANAGER_AUTH_TOKEN_AUDIENCE";

    /// <summary>Builds a bearer token for a user with the given role value (see <c>Roles</c>).</summary>
    public static string ForRole(int role) => For(1, role);

    /// <summary>Builds a bearer token for a specific person id and role value (see <c>Roles</c>).</summary>
    public static string For(int id, int role)
    {
        var claims = new AuthenticatedUser(id, role).ToTokenClaims();

        var configuration = new JwtConfiguration(
            3600,
            Environment.GetEnvironmentVariable(IssuerVariable) ?? string.Empty,
            Environment.GetEnvironmentVariable(AudienceVariable) ?? string.Empty,
            Environment.GetEnvironmentVariable(SecretVariable) ?? string.Empty,
            claims);

        return new JwtHandler().CreateToken(configuration);
    }
}
