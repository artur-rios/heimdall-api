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
        int Concurrency, TimeSpan Duration, string ReportPath);

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

        var scenarios = new (string Name, Func<HttpClient, Task<HttpResponseMessage>> Request)[]
        {
            ("GET /HealthCheck", http => http.GetAsync("/HealthCheck")),
            ("GET /api/scopes", http => http.GetAsync("/api/scopes?pageNumber=1&pageSize=25"))
        };

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

    private static async Task<string> SignInAsync(HttpClient client, Options options)
    {
        using var response = await client.PostAsJsonAsync("/api/auth/login", new
        {
            email = options.Email, password = options.Password, scopeId = options.ScopeId
        });

        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

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
            "| Scenario | Requests | Req/s | p50 (ms) | p95 (ms) | p99 (ms) | Max (ms) | Non-2xx | Faulted |",
            "| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |"
        };

        foreach (var (name, samples, seconds) in results)
        {
            var sorted = samples.Select(s => s.Milliseconds).OrderBy(m => m).ToList();

            lines.Add(
                $"| `{name}` | {samples.Count} | {F(samples.Count / seconds)} | " +
                $"{F(Percentile(sorted, 50))} | {F(Percentile(sorted, 95))} | {F(Percentile(sorted, 99))} | " +
                $"{F(sorted.Count > 0 ? sorted[^1] : 0)} | " +
                $"{samples.Count(s => !s.Faulted && ((int)s.Status < 200 || (int)s.Status >= 300))} | " +
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
            [--scope <guid>] [--concurrency 32] [--seconds 30] [--report load-test.md]

        The account must be able to log in without a second factor. Point this at a deployment you
        are allowed to load: it issues as many requests as it can for the whole duration.
        """;

    private static Options Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Unexpected argument '{args[i]}'.");
            }

            if (i + 1 >= args.Length)
            {
                throw new ArgumentException($"'{args[i]}' needs a value.");
            }

            values[args[i][2..]] = args[++i];
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
            values.TryGetValue("report", out var report) ? report : "load-test.md");
    }
}
