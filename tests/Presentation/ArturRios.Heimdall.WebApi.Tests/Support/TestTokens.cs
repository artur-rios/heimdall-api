using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.WebApi.Security;
using ArturRios.Jwt;
using Npgsql;

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
///     <para>
///         The filter also refuses a person token whose security stamp is not the person's current
///         one (FR-AU-11), so <see cref="For" /> and <see cref="ForMfaPending" /> read the stamp from
///         the database as the issuers would: a token minted before a credential change carries the
///         stamp it was minted with, and is refused after the change, exactly like a real one. A
///         subject that is not a person — a Google User, or nobody — gets no stamp.
///     </para>
/// </remarks>
public static class TestTokens
{
    private const string SecretVariable = "HEIMDALL_AUTH_TOKEN_SECRET";
    private const string IssuerVariable = "HEIMDALL_AUTH_TOKEN_ISSUER";
    private const string AudienceVariable = "HEIMDALL_AUTH_TOKEN_AUDIENCE";
    private const string ConnectionStringVariable = "HEIMDALL_DATA_CONNECTIONSTRING";

    /// <summary>
    ///     Builds a bearer token for the stand-in person of the given role value (see <c>Roles</c>),
    ///     for tests that only care that the caller holds the role. The stand-in owns no scope and
    ///     belongs to none, so it carries exactly the authority the role alone confers.
    /// </summary>
    public static string ForRole(int role) => For(PostgresFixture.StandInPersonIds[(Roles)role], role);

    /// <summary>Builds a bearer token for a specific person's <c>PublicId</c> and role value.</summary>
    public static string For(
        Guid personId, int role, Guid? scopeId = null, params Guid[] ownedScopeIds) =>
        Create(new IdentityUser(personId, role, scopeId, ownedScopeIds) { SecurityStamp = StampOf(personId) });

    /// <summary>
    ///     Builds a UC-38 challenge-shaped bearer token — <c>MfaPending</c> set, no scope claims, the
    ///     challenge audience — for tests proving <see cref="MfaPendingGuardFilter" /> rejects one
    ///     everywhere except <c>POST /api/auth/2fa/verify</c> (FR-2F-10).
    /// </summary>
    /// <param name="personId">The <c>PublicId</c> the challenge names.</param>
    /// <param name="role">The role value the challenge carries.</param>
    /// <param name="lifetime">
    ///     How long the token stays valid, for tests that pin the edges of the window rather than
    ///     what is inside it. The production lifetime is
    ///     <c>TwoFactorLifetimes.ChallengeToken</c>; a test that cares about the edge cannot wait ten
    ///     minutes to reach it, so it mints a token already that close to its own. Omitted, the token
    ///     behaves like any other test credential and outlives the test.
    /// </param>
    /// <param name="challengeId">
    ///     The challenge the token names (FR-2F-10). Omitted, it is <see cref="SeededChallengeId" />,
    ///     which a test seeding a two-factor configuration directly records as outstanding — the
    ///     state a login would have left.
    /// </param>
    public static string ForMfaPending(
        Guid personId, int role, TimeSpan? lifetime = null, Guid? challengeId = null) =>
        Create(
            new IdentityUser(personId, role)
            {
                MfaPending = true,
                ChallengeId = challengeId ?? SeededChallengeId,
                SecurityStamp = StampOf(personId)
            },
            lifetime,
            Audiences.TwoFactorChallenge);

    /// <summary>
    ///     Builds a token for <paramref name="user" /> under an audience of the test's choosing, signed
    ///     with the host's real key — for tests proving that a token whose audience and claims disagree
    ///     is refused (TH-06, TH-35), which no issuer in the host would ever produce.
    /// </summary>
    public static string WithAudience(IdentityUser user, string audience) => Create(user, audience: audience);

    /// <summary>
    ///     Builds a token for <paramref name="user" /> under an issuer and an audience of the test's
    ///     choosing, signed with the host's real key — for tests proving that a correctly signed token
    ///     Heimdall did not issue, or did not issue for itself, is refused (Threat Model TB-5).
    /// </summary>
    public static string WithOrigin(IdentityUser user, string issuer, string audience) =>
        Create(user, audience: audience, issuer: issuer);

    /// <summary>
    ///     The issuer the host under test issues every token under, as <see cref="PostgresFixture" />
    ///     publishes it.
    /// </summary>
    public static string Issuer => Environment.GetEnvironmentVariable(IssuerVariable) ?? string.Empty;

    /// <summary>
    ///     The audiences the host under test issues person tokens under, derived from the person
    ///     audience <see cref="PostgresFixture" /> publishes, as the host derives them.
    /// </summary>
    public static PersonTokenAudiences Audiences =>
        new(Environment.GetEnvironmentVariable(AudienceVariable) ?? string.Empty);

    /// <summary>
    ///     The outstanding challenge a seeded two-factor configuration records, and the one
    ///     <see cref="ForMfaPending" /> names by default. One value is enough: each person has their
    ///     own configuration, so sharing it lets no test redeem another's challenge.
    /// </summary>
    public static readonly Guid SeededChallengeId = Guid.Parse("6f1c2a52-0d4e-4c1b-9a51-2f5e8b3c7d10");

    /// <summary>
    ///     The person's current security stamp (FR-AU-11), or <c>null</c> when no person has this
    ///     <c>PublicId</c> or no database has been published yet — the stamp a token issued now would
    ///     carry.
    /// </summary>
    public static Guid? StampOf(Guid personId)
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionStringVariable);

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return null;
        }

        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();

        using var command = new NpgsqlCommand(
            "SELECT security_stamp FROM heimdall.person WHERE public_id = @id", connection);
        command.Parameters.AddWithValue("id", personId);

        return command.ExecuteScalar() as Guid?;
    }

    private static string Create(
        IdentityUser user, TimeSpan? lifetime = null, string? audience = null, string? issuer = null)
    {
        var configuration = new JwtConfiguration(
            lifetime?.TotalSeconds ?? 3600,
            issuer ?? Issuer,
            audience ?? Audiences.Person,
            Environment.GetEnvironmentVariable(SecretVariable) ?? string.Empty,
            new IdentityUserMapper(Audiences).ToClaims(user));

        return new JwtHandler().CreateToken(configuration);
    }
}
