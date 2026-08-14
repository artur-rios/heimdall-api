using ArturRios.Heimdall.Data.Configuration;
using ArturRios.Heimdall.Data.Seeding;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.WebApi.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.PostgreSql;

namespace ArturRios.Heimdall.WebApi.Tests.Support;

/// <summary>
///     Starts a throwaway PostgreSQL container once for the whole functional test suite, applies the
///     EF migrations to it, and exposes its connection string, so functional tests run end-to-end
///     against a real database that closely matches production. Shared via
///     <see cref="FunctionalCollection" /> so the container is created once, not per test class.
///     See docs/Testing Specification Document.md §7.2.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private const string ConnectionStringVariable = "HEIMDALL_DATA_CONNECTIONSTRING";
    private const string DatabaseTypeVariable = "HEIMDALL_DATA_DATABASETYPE";
    private const string TokenSecretVariable = "HEIMDALL_AUTH_TOKEN_SECRET";
    private const string TokenIssuerVariable = "HEIMDALL_AUTH_TOKEN_ISSUER";
    private const string TokenAudienceVariable = "HEIMDALL_AUTH_TOKEN_AUDIENCE";

    /// <summary>E-mail of the master system administrator the API seeds into the container.</summary>
    public const string MasterUserEmail = "master@heimdall.test";

    /// <summary>Password the master system administrator is seeded with.</summary>
    public const string MasterUserPassword = "Str0ng-Master-Pass!";

    /// <summary>
    ///     Secret the suite's stand-in Google ID tokens are signed with (UC-25). Long enough for
    ///     HMAC-SHA256, which refuses a key shorter than its 256-bit output.
    /// </summary>
    public const string GoogleTestSigningSecret = "functional-tests-google-id-token-signing-secret";

    /// <summary>
    ///     <c>PublicId</c>s of the stand-in persons this fixture seeds, one per role, that
    ///     <see cref="TestTokens.ForRole" /> mints its tokens for.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <c>ForRole</c> used to invent a <see cref="Guid" /> per call, which worked while
    ///         authentication was the only thing that read the token: nothing looked the caller up, so
    ///         a person who did not exist was as good as one who did. <c>ActorLivenessFilter</c> now
    ///         rejects a token naming an identity that is absent or logically deleted, which is the
    ///         point of it — and which makes an invented caller a caller the API is right to refuse.
    ///     </para>
    ///     <para>
    ///         These three exist so a test that only cares about a role gate still does not have to
    ///         seed anyone. They are deliberately inert: no scope membership, no scope ownership, so
    ///         they carry exactly the authority the role alone confers, which is what the invented
    ///         Guid conferred before. A test whose behaviour depends on <em>which</em> person is
    ///         acting still seeds its own and passes that <c>PublicId</c> to <c>TestTokens.For</c>.
    ///     </para>
    /// </remarks>
    public static readonly IReadOnlyDictionary<Roles, Guid> StandInPersonIds = new Dictionary<Roles, Guid>
    {
        [Roles.SystemAdmin] = new("00000000-0000-0000-0000-00000000ad01"),
        [Roles.ScopeAdmin] = new("00000000-0000-0000-0000-00000000ad02"),
        [Roles.User] = new("00000000-0000-0000-0000-00000000ad03")
    };

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16-alpine")
        .Build();

    /// <summary>The connection string of the running container's database.</summary>
    public string ConnectionString => _container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        // Point the API under test at the container instead of a developer's local database.
        Environment.SetEnvironmentVariable(ConnectionStringVariable, ConnectionString);
        Environment.SetEnvironmentVariable(DatabaseTypeVariable, "PostgreSql");

        // Startup refuses to boot without a signing secret, since an empty one makes every request
        // fail inside the token validator. This value is for tests only.
        Environment.SetEnvironmentVariable(TokenSecretVariable, "functional-tests-signing-secret-key");
        Environment.SetEnvironmentVariable(TokenIssuerVariable, "heimdall-tests");
        Environment.SetEnvironmentVariable(TokenAudienceVariable, "heimdall-tests");

        // UC-25 verifies Google ID tokens. Publishing this secret makes the host under test resolve
        // LocalGoogleIdTokenVerifier instead of the real one, so the suite exercises sign-up,
        // sign-in, AF-25c, and AF-25d — all of which sit behind verification — without reaching
        // Google. Tokens still have to be validly signed; TestGoogleTokens mints them with this same
        // secret. Never set outside the suite: Startup ignores it in Production and no .env file
        // carries it.
        Environment.SetEnvironmentVariable(
            GoogleSignInOptions.TestSigningSecretVariable, GoogleTestSigningSecret);

        // The seeder refuses to start without a configured master user.
        Environment.SetEnvironmentVariable(MasterUserOptions.NameVariable, "Master User");
        Environment.SetEnvironmentVariable(MasterUserOptions.EmailVariable, MasterUserEmail);
        Environment.SetEnvironmentVariable(MasterUserOptions.PasswordVariable, MasterUserPassword);

        // The API refuses to start against a schema with pending migrations, so apply them here.
        await using var context = CreateContext();

        await context.Database.MigrateAsync();

        // The application's own seeder, run here rather than waited for: it is what creates the ROLE
        // rows, and the stand-in persons below carry a role foreign key. The host under test runs it
        // again on start-up, which is harmless — it is idempotent by design.
        await new DatabaseSeeder(
                context,
                MasterUserOptions.FromEnvironment(),
                NullLogger<DatabaseSeeder>.Instance)
            .SeedAsync();

        await SeedStandInPersonsAsync(context);
    }

    /// <summary>
    ///     Inserts the <see cref="StandInPersonIds" /> persons, so a token minted by
    ///     <see cref="TestTokens.ForRole" /> names somebody who actually exists.
    /// </summary>
    private static async Task SeedStandInPersonsAsync(AppDbContext context)
    {
        foreach (var (role, publicId) in StandInPersonIds)
        {
            if (await context.Persons.AnyAsync(person => person.PublicId == publicId))
            {
                continue;
            }

            context.Persons.Add(new Person
            {
                PublicId = publicId,
                Name = $"Stand-in {role}",
                // Distinct from every address a test seeds, and — for the two administrator roles —
                // from each other, which the ux_person_admin_email index now requires.
                Email = $"stand-in-{role}@functional.test".ToLowerInvariant(),
                RoleId = (long)role,
                EmailVerified = true
            });
        }

        await context.SaveChangesAsync();
    }

    /// <summary>
    ///     Clears the TOTP time step a person's configuration has already accepted, so a test can
    ///     present a fresh app code without waiting out the step it just spent.
    /// </summary>
    /// <remarks>
    ///     An app code is single-use (<c>TotpCodeVerifier</c>): the step it was accepted at is
    ///     recorded, and a code from that step or earlier is refused. A test that confirms setup with
    ///     the current code and then immediately disables or regenerates with it is presenting the
    ///     same code twice inside one 30-second step — which is the replay the guard exists to
    ///     refuse, and which a real caller would never do, since a full second passes between their
    ///     two requests only by coincidence. Rather than sleeping out the step, the test forgets it;
    ///     the guard itself is covered directly by <c>AuthControllerVerifyTwoFactorAuthTests</c>'s
    ///     replay test.
    /// </remarks>
    public async Task ForgetLastTotpStepAsync(Guid personPublicId)
    {
        await using var context = CreateContext();

        var configuration = await context.TwoFactorAuths
            .FirstAsync(x => x.Person.PublicId == personPublicId);

        configuration.LastTotpTimeStepUsed = null;

        await context.SaveChangesAsync();
    }

    /// <summary>
    ///     Creates a context bound to the container, for tests that assert on database state
    ///     directly rather than through the API.
    /// </summary>
    public AppDbContext CreateContext() => new(
        new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(ConnectionString).Options,
        NullLoggerFactory.Instance,
        DbContextDiagnosticsOptions.Disabled);

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();
}
