using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace ArturRios.Heimdall.LoadTest;

/// <summary>
///     Drives a running Heimdall deployment at a stated concurrency for a stated duration and
///     reports what it delivered: throughput, latency percentiles, and how many requests failed.
/// </summary>
/// <remarks>
///     <para>
///         This exists because NFR-05 used to promise a response time "under normal load" while
///         nothing in the repository generated load, so the phrase could be neither met nor missed.
///         Rather than define load in prose, this measures the API under some and the requirement
///         quotes the result.
///     </para>
///     <para>
///         It talks to a deployment over HTTP rather than hosting the API in-process, which is the
///         whole point: an in-process test server measures handler time and hides Kestrel, the
///         socket, TLS, connection reuse and the real connection pool — every part of the path that
///         behaves differently when many callers arrive at once. What it measures is therefore
///         closer to what a caller experiences, and correspondingly dependent on where it is run
///         from.
///     </para>
///     <para>
///         It is not a benchmark harness and does not pretend to be one: no coordinated-omission
///         correction, no warmup modelling beyond a discarded opening phase, no statistical
///         confidence claims. It answers one question — does the API still answer, and how quickly,
///         while N callers are asking at once — which is the question NFR-05 needed and did not have.
///     </para>
/// </remarks>
public static class Program
{
    private sealed record Options(
        Uri BaseUrl, string Email, string Password, Guid? ScopeId,
        int Concurrency, TimeSpan Duration, string ReportPath, bool IncludeWrites);

    private sealed record Sample(double Milliseconds, HttpStatusCode Status, bool Faulted);

    public static async Task<int> Main(string[] args)
    {
        Options options;

        try
        {
            options = Parse(args);
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine(exception.Message);
            Console.Error.WriteLine();
            Console.Error.WriteLine(Usage);

            return 1;
        }

        Console.WriteLine($"Target      : {options.BaseUrl}");
        Console.WriteLine($"Concurrency : {options.Concurrency}");
        Console.WriteLine($"Duration    : {options.Duration.TotalSeconds:F0}s");

        if (options.IncludeWrites)
        {
            Console.WriteLine("Writes      : ON — this run creates a person and a scope per request");
        }

        Console.WriteLine();

        using var handler = new SocketsHttpHandler
        {
            // Well above the concurrency, so callers are never queued behind a connection limit.
            // Measuring the client's own contention rather than the API's would be worse than not
            // measuring at all: it would look exactly like a slow server.
            MaxConnectionsPerServer = Math.Max(64, options.Concurrency * 2),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        };

        using var client = new HttpClient(handler) { BaseAddress = options.BaseUrl, Timeout = TimeSpan.FromSeconds(30) };

        string token;

        try
        {
            token = await SignInAsync(client, options);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Could not sign in as {options.Email}: {exception.Message}");

            return 1;
        }

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var scenarios = new List<(string Name, Func<HttpClient, Task<HttpResponseMessage>> Request)>
        {
            ("GET /HealthCheck", http => http.GetAsync("/HealthCheck")),
            ("GET /api/scopes", http => http.GetAsync("/api/scopes?pageNumber=1&pageSize=25")),

            // Included by default even though the rate limiter sheds nearly all of it, because what
            // it measures is the shedding. See the note on the 429 column in Report.
            ("POST /api/auth/login", http => http.PostAsJsonAsync("/api/auth/login", new
            {
                email = options.Email, password = options.Password, scopeId = options.ScopeId
            }))
        };

        if (options.IncludeWrites)
        {
            Guid ownerId;

            try
            {
                ownerId = await CreateOwnerAsync(client);
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"Could not create the owner the write scenario needs: {exception.Message}");

                return 1;
            }

            // A fresh scope per request rather than repeated updates of one row. Updating a single
            // row from every caller would measure PostgreSQL's row lock, which is a property of the
            // database rather than of this API, and would report contention as latency.
            scenarios.Add(("POST /api/scopes", http => http.PostAsJsonAsync("/api/scopes", new
            {
                name = $"load-test-{Guid.NewGuid():N}",
                description = "Created by ArturRios.Heimdall.LoadTest.",
                ownerIds = new[] { ownerId }
            })));
        }

        var results = new List<(string Name, IReadOnlyList<Sample> Samples, double Seconds)>();

        // A fifth of the run, between two and ten seconds. Enough for the JIT, the EF model and the
        // first query plans, which a cold deployment pays heavily for: an unwarmed run measured this
        // API's authenticated read at nearly twice its warm median, which would be published as a
        // regression by anyone who did not know the container had just started.
        var warmUp = TimeSpan.FromSeconds(Math.Clamp(options.Duration.TotalSeconds / 5, 2, 10));

        foreach (var (name, request) in scenarios)
        {
            Console.WriteLine($"Warming {name} for {warmUp.TotalSeconds:F0}s …");

            await RunAsync(client, request, options, warmUp);

            Console.WriteLine($"Running {name} …");

            results.Add((name, await RunAsync(client, request, options, options.Duration), options.Duration.TotalSeconds));
        }

        Report(options, results);

        var failed = results.Sum(r => r.Samples.Count(s => s.Faulted || (int)s.Status >= 500));

        Console.WriteLine();
        Console.WriteLine($"Report written to {options.ReportPath}");

        if (failed > 0)
        {
            Console.Error.WriteLine($"{failed} request(s) faulted or answered 5xx — the run is not a clean baseline.");

            return 1;
        }

        return 0;
    }

    /// <summary>
    ///     Holds <paramref name="options" />' concurrency in flight for <paramref name="duration" />,
    ///     each worker issuing requests back to back.
    /// </summary>
    /// <remarks>
    ///     A closed-loop model: every worker waits for its own response before issuing the next, so
    ///     the offered rate falls when the API slows rather than queueing without bound. That
    ///     understates the latency a fixed-rate arrival would see — the coordinated-omission problem
    ///     — and is stated here rather than corrected for, because the figure this produces is meant
    ///     to be compared against itself across runs, not published as a service level.
    /// </remarks>
    private static async Task<IReadOnlyList<Sample>> RunAsync(
        HttpClient client, Func<HttpClient, Task<HttpResponseMessage>> request, Options options, TimeSpan duration)
    {
        using var deadline = new CancellationTokenSource(duration);

        var samples = new List<Sample>[options.Concurrency];

        var workers = Enumerable.Range(0, options.Concurrency).Select(async index =>
        {
            var mine = samples[index] = [];

            while (!deadline.IsCancellationRequested)
            {
                var stopwatch = Stopwatch.StartNew();

                try
                {
                    using var response = await request(client);
                    await response.Content.ReadAsByteArrayAsync();
                    stopwatch.Stop();

                    mine.Add(new Sample(stopwatch.Elapsed.TotalMilliseconds, response.StatusCode, false));
                }
                catch (Exception)
                {
                    stopwatch.Stop();

                    // A timeout or a dropped connection is a result, not a reason to stop: a run
                    // that abandoned itself on the first failure would report nothing about the
                    // condition worth reporting on.
                    mine.Add(new Sample(stopwatch.Elapsed.TotalMilliseconds, HttpStatusCode.RequestTimeout, true));
                }
            }
        });

        await Task.WhenAll(workers);

        return samples.SelectMany(s => s).ToList();
    }

    /// <summary>
    ///     Creates the ScopeAdmin the write scenario names as the new scope's owner, and returns its
    ///     PublicId.
    /// </summary>
    /// <remarks>
    ///     Once, in setup, rather than per request: creating a person hashes a password with
    ///     Argon2id, and doing that inside the measured loop would make the write scenario a second
    ///     measurement of the hash rather than of the write path.
    /// </remarks>
    private static async Task<Guid> CreateOwnerAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync("/api/persons", new
        {
            name = "Load Test Owner",
            email = $"load-test-owner-{Guid.NewGuid():N}@heimdall.test",
            password = $"Ld-{Guid.NewGuid():N}!aA1",
            role = 2 // ScopeAdmin: a scope's owner must hold it, and this account is never signed in as.
        });

        var body = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"{(int)response.StatusCode} from POST /api/persons: {body}");
        }

        using var document = JsonDocument.Parse(body);

        return document.RootElement.GetProperty("data").GetProperty("id").GetGuid();
    }

    /// <summary>
    ///     Signs in for the bearer token every authenticated scenario needs, waiting out the rate
    ///     limiter if it has to.
    /// </summary>
    /// <remarks>
    ///     The retry is not defensive padding: this tool's own login scenario spends the whole
    ///     per-IP permit budget, so a second run started within the limiter's window cannot sign in
    ///     at all. Without this, the harness measured the API once and then refused to start until
    ///     somebody worked out why — a failure caused entirely by the previous run.
    /// </remarks>
    private static async Task<string> SignInAsync(HttpClient client, Options options)
    {
        const int attempts = 6;

        HttpResponseMessage response;

        for (var attempt = 1; ; attempt++)
        {
            response = await client.PostAsJsonAsync("/api/auth/login", new
            {
                email = options.Email, password = options.Password, scopeId = options.ScopeId
            });

            if (response.StatusCode != HttpStatusCode.TooManyRequests || attempt >= attempts)
            {
                break;
            }

            // Retry-After when the deployment sends one; otherwise a guess wide enough to outlast a
            // fixed window that has already been spent.
            var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(15);

            response.Dispose();

            Console.WriteLine(
                $"Sign-in shed by the rate limiter; waiting {wait.TotalSeconds:F0}s " +
                $"(attempt {attempt} of {attempts - 1}) …");

            await Task.Delay(wait);
        }

        using (response)
        {
            response.EnsureSuccessStatusCode();

            return ReadToken(await response.Content.ReadAsStringAsync());
        }
    }

    private static string ReadToken(string body)
    {
        using var document = JsonDocument.Parse(body);

        var data = document.RootElement.GetProperty("data");

        if (data.ValueKind is JsonValueKind.Null ||
            !data.TryGetProperty("token", out var token) ||
            token.ValueKind is JsonValueKind.Null)
        {
            // A two-factor-gated account answers with a challenge instead. Load-testing through a
            // second factor would measure the operator's authenticator app, so it is refused rather
            // than worked around.
            throw new InvalidOperationException(
                "the response carried no token — an account with two-factor authentication cannot drive a load run");
        }

        return token.GetString()!;
    }

    private static double Percentile(IReadOnlyList<double> sorted, double percentile)
    {
        if (sorted.Count == 0)
        {
            return 0;
        }

        var rank = (int)Math.Ceiling(percentile / 100 * sorted.Count) - 1;

        return sorted[Math.Clamp(rank, 0, sorted.Count - 1)];
    }

    private static void Report(
        Options options, IReadOnlyList<(string Name, IReadOnlyList<Sample> Samples, double Seconds)> results)
    {
        string F(double value) => value.ToString("F1", CultureInfo.InvariantCulture);

        var lines = new List<string>
        {
            "# Load test results",
            "",
            $"Target `{options.BaseUrl}`, {options.Concurrency} concurrent callers, " +
            $"{options.Duration.TotalSeconds:F0}s per scenario, from {Environment.MachineName}.",
            "",
            "Each scenario is warmed at the same concurrency first and those samples discarded.",
            "",
            "Closed loop: each caller waits for its own response before issuing the next, so the",
            "offered rate falls as the API slows. Latencies are therefore optimistic against a",
            "fixed-rate arrival — compare runs with each other, not with a service level.",
            "",
            "Throughput and percentiles cover the requests the API *answered* — 2xx only. Attempts",
            "counts everything sent, including what the rate limiter shed.",
            "",
            "That distinction is not pedantry. A shed request is refused in under a millisecond, so a",
            "scenario the limiter sheds would otherwise report a sub-millisecond median describing",
            "the speed of the refusal, and a throughput figure counting refusals as work. Both would",
            "be excellent and neither would mean anything.",
            "",
            "| Scenario | Attempts | 2xx | 2xx/s | p50 (ms) | p95 (ms) | p99 (ms) | Max (ms) | 429 | Other non-2xx | Faulted |",
            "| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |"
        };

        foreach (var (name, samples, seconds) in results)
        {
            var answered = samples.Where(s => !s.Faulted && (int)s.Status is >= 200 and < 300).ToList();
            var sorted = answered.Select(s => s.Milliseconds).OrderBy(m => m).ToList();
            var shed = samples.Count(s => s.Status == HttpStatusCode.TooManyRequests);

            // A scenario the API answered nothing in has no latency to report, and printing 0.0
            // would read as instantaneous rather than as absent.
            string Latency(double value) => sorted.Count > 0 ? F(value) : "—";

            lines.Add(
                $"| `{name}` | {samples.Count} | {answered.Count} | {F(answered.Count / seconds)} | " +
                $"{Latency(Percentile(sorted, 50))} | {Latency(Percentile(sorted, 95))} | " +
                $"{Latency(Percentile(sorted, 99))} | {Latency(sorted.Count > 0 ? sorted[^1] : 0)} | {shed} | " +
                $"{samples.Count(s => !s.Faulted && s.Status != HttpStatusCode.TooManyRequests &&
                                      (int)s.Status is < 200 or >= 300)} | " +
                $"{samples.Count(s => s.Faulted)} |");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.ReportPath))!);
        File.WriteAllLines(options.ReportPath, lines);

        Console.WriteLine();

        // The table only: the prose above it is written for whoever reads the report later, not for
        // whoever is watching the run. Selected by shape rather than by a line count, which silently
        // starts printing the wrong thing the moment a line is added above.
        foreach (var line in lines.SkipWhile(line => !line.StartsWith('|')))
        {
            Console.WriteLine(line);
        }
    }

    private const string Usage = """
        Usage:
          dotnet run --project tools/ArturRios.Heimdall.LoadTest -- \
            --url http://localhost:8080 --email admin@example.com --password '…' \
            [--scope <guid>] [--concurrency 32] [--seconds 30] [--report load-test.md] [--write]

        The account must be able to log in without a second factor. Point this at a deployment you
        are allowed to load: it issues as many requests as it can for the whole duration.

        --write adds a scenario that creates a scope per request, and a ScopeAdmin to own them. It
        is off by default because it is the only part of this that leaves anything behind, and the
        rows it leaves are proportional to how fast the API is — tens of thousands of them. Use it
        against a deployment you can afterwards throw away.
        """;

    /// <summary>Options that take no value. Everything else must be followed by one.</summary>
    private static readonly HashSet<string> SwitchOptions = new(StringComparer.OrdinalIgnoreCase) { "write" };

    private static Options Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Unexpected argument '{args[i]}'.");
            }

            var name = args[i][2..];

            // A switch: last argument, or followed by another `--option`. Without this, `--write`
            // would swallow whatever came after it as its value — silently, and most damagingly when
            // that value was the next option's.
            if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                if (!SwitchOptions.Contains(name))
                {
                    throw new ArgumentException($"'{args[i]}' needs a value.");
                }

                values[name] = "true";

                continue;
            }

            values[name] = args[++i];
        }

        string Required(string name) => values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException($"--{name} is required.");

        return new Options(
            new Uri(Required("url"), UriKind.Absolute),
            Required("email"),
            Required("password"),
            values.TryGetValue("scope", out var scope) ? Guid.Parse(scope) : null,
            values.TryGetValue("concurrency", out var concurrency) ? int.Parse(concurrency) : 32,
            TimeSpan.FromSeconds(values.TryGetValue("seconds", out var seconds) ? int.Parse(seconds) : 30),
            values.TryGetValue("report", out var report) ? report : "load-test.md",
            values.ContainsKey("write"));
    }
}
