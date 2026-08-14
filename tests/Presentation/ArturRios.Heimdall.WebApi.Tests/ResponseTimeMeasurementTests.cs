using System.Diagnostics;
using System.Globalization;
using System.Net;
using ArturRios.Configuration.Enums;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Query.Output;
using ArturRios.Heimdall.WebApi.Tests.Support;
using ArturRios.Output;
using ArturRios.Util.Hashing;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Functional;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Heimdall.WebApi.Tests;

// Measurement behind NFR-05.
//
// NFR-05 used to read "the API shall respond to requests within 500 ms under normal load" — a number
// nobody had measured, against a load nobody had defined, and one the authentication design
// contradicts on purpose: a password check is a full Argon2id verification, which is expensive
// because that is what makes an offline attack on a stolen hash costly.
//
// These tests measure what the API actually delivers, per class of endpoint, and assert a ceiling
// well above the measured figure. They are not benchmarks and the numbers here are not a promise
// about production hardware: a functional test runs against a container on whatever the developer or
// runner happens to have. The ceilings are set so they cannot fail on ordinary variance and cannot
// pass through an order-of-magnitude regression — an accidental N+1 in a listing, or a password
// check that started running twice.
//
// The measured figures are written to TestResults/response-times.md so the number in the requirement
// can be traced back to a run rather than to somebody's recollection. See the Testing Specification,
// §11.
[Collection(nameof(FunctionalCollection))]
public class ResponseTimeMeasurementTests(PostgresFixture db) : WebApiTest<Program>(EnvironmentType.Local)
{
    private const string Password = "Str0ng-Timing-Pass!";

    /// <summary>Requests per measurement, after a warm-up that is not counted.</summary>
    private const int Samples = 12;

    /// <summary>
    ///     Discarded before measuring. The first request through a freshly built host pays for JIT,
    ///     the EF model, the connection pool and the first query plan — real costs, but ones a caller
    ///     meets once per process rather than per request, so counting them would describe start-up
    ///     rather than response time.
    /// </summary>
    private const int WarmUp = 3;

    private static readonly List<(string Endpoint, string Class, double Median, double Slowest)> Measurements = [];

    private static string UniqueEmail(string prefix) => $"{prefix}-{Guid.NewGuid():N}@test.local";

    private async Task<Scope> SeedScopeAsync()
    {
        await using var context = db.CreateContext();
        var scope = new Scope { PublicId = Guid.NewGuid(), Name = $"timing-{Guid.NewGuid():N}" };
        context.Scopes.Add(scope);
        await context.SaveChangesAsync();
        return scope;
    }

    private async Task<Person> SeedAdminAsync(string email)
    {
        await using var context = db.CreateContext();
        var person = new Person
        {
            PublicId = Guid.NewGuid(),
            Name = "Timing Admin",
            Email = email,
            PasswordHash = Hash.EncodeWithRandomSalt(Password, out var salt),
            Salt = salt,
            RoleId = (long)Roles.SystemAdmin,
            EmailVerified = true
        };
        context.Persons.Add(person);
        await context.SaveChangesAsync();
        return person;
    }

    private async Task SeedUsersAsync(Scope scope, int count)
    {
        await using var context = db.CreateContext();

        for (var i = 0; i < count; i++)
        {
            var person = new Person
            {
                PublicId = Guid.NewGuid(),
                Name = $"Member {i:D3}",
                Email = UniqueEmail($"member-{i}"),
                PasswordHash = [1],
                Salt = [1],
                RoleId = (long)Roles.User,
                ScopeId = scope.Id
            };
            context.Persons.Add(person);
            await context.SaveChangesAsync();

            context.ScopeUsers.Add(new ScopeUser { ScopeId = scope.Id, PersonId = person.Id });
            await context.SaveChangesAsync();
        }
    }

    /// <summary>
    ///     Runs <paramref name="request" /> <see cref="WarmUp" /> + <see cref="Samples" /> times and
    ///     records the median and the slowest sample of the counted runs.
    /// </summary>
    /// <remarks>
    ///     The median rather than the mean: one scheduling hiccup on a shared runner should not move
    ///     the figure the requirement is written from. The slowest is recorded alongside it because a
    ///     requirement that only quoted a median would say nothing about the request a caller
    ///     actually waits on when the machine is busy.
    /// </remarks>
    private static async Task<(double Median, double Slowest)> MeasureAsync(
        string endpoint, string endpointClass, Func<Task<HttpStatusCode>> request)
    {
        for (var i = 0; i < WarmUp; i++)
        {
            Assert.True((int)await request() < 500, $"{endpoint} failed during warm-up");
        }

        var timings = new List<double>(Samples);

        for (var i = 0; i < Samples; i++)
        {
            var stopwatch = Stopwatch.StartNew();
            var status = await request();
            stopwatch.Stop();

            Assert.True((int)status < 500, $"{endpoint} answered {status} while being measured");
            timings.Add(stopwatch.Elapsed.TotalMilliseconds);
        }

        timings.Sort();

        var median = timings[timings.Count / 2];
        var slowest = timings[^1];

        Measurements.Add((endpoint, endpointClass, median, slowest));
        Report();

        return (median, slowest);
    }

    /// <summary>
    ///     Rewrites the report after every measurement, so a run that is interrupted still leaves the
    ///     figures it had reached rather than nothing.
    /// </summary>
    private static void Report()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "TestResults");
        Directory.CreateDirectory(path);

        var lines = new List<string>
        {
            "# Measured response times",
            "",
            $"Recorded by `ResponseTimeMeasurementTests` on {Environment.MachineName}, " +
            $"{Environment.ProcessorCount} logical processors, against the suite's PostgreSQL container.",
            "",
            $"Median and slowest of {Samples} samples, after {WarmUp} discarded warm-up requests.",
            "",
            "| Endpoint | Class | Median (ms) | Slowest (ms) |",
            "| --- | --- | ---: | ---: |"
        };

        lines.AddRange(Measurements
            .OrderBy(m => m.Class)
            .ThenBy(m => m.Endpoint)
            .Select(m => $"| `{m.Endpoint}` | {m.Class} | {m.Median.ToString("F1", CultureInfo.InvariantCulture)} " +
                         $"| {m.Slowest.ToString("F1", CultureInfo.InvariantCulture)} |"));

        File.WriteAllLines(Path.Combine(path, "response-times.md"), lines);
    }

    [FunctionalFact]
    public async Task MeasureLiveness()
    {
        // The floor: no authentication, no database, no work. Whatever this costs is the pipeline
        // itself, and every other figure includes it.
        var (median, slowest) = await MeasureAsync("GET /HealthCheck", "unauthenticated", async () =>
            (await Gateway.GetAsync<DataOutput<string?>>("/HealthCheck")).StatusCode);

        Assert.True(median < 150, $"liveness median was {median:F1} ms");
        Assert.True(slowest < 400, $"liveness slowest was {slowest:F1} ms");
    }

    [FunctionalFact]
    public async Task MeasureAuthenticatedRead()
    {
        // A token-authenticated read: claims parsing, the actor-liveness lookup, one indexed query.
        var person = await SeedAdminAsync(UniqueEmail("read"));
        Authorize(TestTokens.For(person.PublicId, (int)Roles.SystemAdmin));

        var (median, slowest) = await MeasureAsync("GET /api/persons/{id}", "authenticated read", async () =>
            (await Gateway.GetAsync<DataOutput<PersonOutput?>>($"/api/persons/{person.PublicId}")).StatusCode);

        Assert.True(median < 150, $"read median was {median:F1} ms");
        Assert.True(slowest < 400, $"read slowest was {slowest:F1} ms");
    }

    [FunctionalFact]
    public async Task MeasurePaginatedList()
    {
        // A page out of a populated table, which is where an accidental N+1 would show up: the
        // projection reaches through ScopeMembership and ScopeOwnerships for every row.
        var scope = await SeedScopeAsync();
        await SeedUsersAsync(scope, 50);

        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        var (median, slowest) = await MeasureAsync(
            "GET /api/scopes/{id}/persons", "authenticated list (50 rows, page of 25)", async () =>
                (await Gateway.GetAsync<PaginatedOutput<PersonOutput>>(
                    $"/api/scopes/{scope.PublicId}/persons?pageNumber=1&pageSize=25")).StatusCode);

        Assert.True(median < 250, $"list median was {median:F1} ms");
        Assert.True(slowest < 600, $"list slowest was {slowest:F1} ms");
    }

    [FunctionalFact]
    public async Task MeasureWrite()
    {
        // A write with validation, two uniqueness reads, an insert and an audit entry.
        var scope = await SeedScopeAsync();
        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        var (median, slowest) = await MeasureAsync(
            "POST /api/scopes/{id}/permissions", "authenticated write", async () =>
                (await Gateway.PostAsync<DataOutput<CreateScopePermissionCommandOutput?>>(
                    $"/api/scopes/{scope.PublicId}/permissions",
                    new CreateScopePermissionCommand { Name = $"perm-{Guid.NewGuid():N}" })).StatusCode);

        Assert.True(median < 250, $"write median was {median:F1} ms");
        Assert.True(slowest < 600, $"write slowest was {slowest:F1} ms");
    }

    [FunctionalFact]
    public async Task MeasureLogin()
    {
        // The expensive one, and expensive on purpose. A login verifies the password with Argon2id,
        // whose cost is the point: it is what makes an offline attack on a stolen hash slow. Every
        // rejection pays it too, including one for an address that belongs to nobody (FR-AU-10),
        // because a cheaper "no such account" would answer by timing the question the uniform 401
        // refuses to answer.
        //
        // No ceiling below a second is asserted here. The cost is set by the hashing library's
        // parameters, not by this API, and pinning it would turn a deliberate security property into
        // a test that fails when it is strengthened. The figure is measured and published instead.
        var email = UniqueEmail("login");
        await SeedAdminAsync(email);

        var (median, slowest) = await MeasureAsync("POST /api/auth/login", "password verification", async () =>
            (await Gateway.PostAsync<DataOutput<LoginCommandOutput?>>(
                "/api/auth/login", new LoginCommand { Email = email, Password = Password })).StatusCode);

        Assert.True(median < 5000, $"login median was {median:F1} ms, far beyond the expected cost");
        Assert.True(slowest < 10000, $"login slowest was {slowest:F1} ms");
    }
}
