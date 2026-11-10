using System.Text.Json;
using System.Text.Json.Serialization;
using ArturRios.Heimdall.Query.HealthChecks;

namespace ArturRios.Heimdall.WebApi.Monitoring;

/// <summary>
///     The detailed health report, served only on the metrics port. Traefik routes the application port alone
///     and nothing publishes the metrics port, so yggdrasil's status API reads it without a credential. For
///     the same reason it carries only names, statuses and reason words.
/// </summary>
public static class InternalHealthEndpoint
{
    /// <summary>The most one check may take, whether or not it listens for cancellation.</summary>
    public static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(3);

    /// <summary>How long a report is reused, counted from when its checks finished.</summary>
    public static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(5);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static async Task Handle(HttpContext context)
    {
        try
        {
            var report = await context.RequestServices.GetRequiredService<Reports>().GetAsync(context.RequestAborted);

            context.Response.StatusCode = report.Status == HealthStatuses.Unhealthy
                ? StatusCodes.Status503ServiceUnavailable
                : StatusCodes.Status200OK;
            context.Response.ContentType = "application/json";

            await JsonSerializer.SerializeAsync(context.Response.Body, report, Json, context.RequestAborted);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // The caller left. This branch runs ahead of ExceptionMiddleware, and a caller must not be able
            // to write to the log by hanging up.
        }
    }

    /// <summary>
    ///     The report, shared by every caller. The endpoint has no credential, so any internal container can
    ///     call it in a loop: a report is kept for <see cref="CacheFor" /> from when its checks finished, and
    ///     callers who arrive during a run wait for it instead of starting their own. The run is on no
    ///     request's cancellation, so a caller who gives up does not cancel it for the rest.
    /// </summary>
    /// <remarks>
    ///     A singleton over checks that are scoped (the database one uses a repository), so each run makes a
    ///     scope of its own.
    /// </remarks>
    public sealed class Reports(
        IServiceScopeFactory scopes, TimeProvider clock, ILogger<Reports> logger)
    {
        private readonly object _gate = new();

        // Touched only inside a run, and runs never overlap.
        private readonly Dictionary<string, string> _lastStatus = [];

        private Task<Report>? _inFlight;
        private Report? _last;
        private DateTimeOffset _finishedAt;

        internal async Task<Report> GetAsync(CancellationToken cancellationToken)
        {
            Task<Report> run;

            lock (_gate)
            {
                if (_inFlight is null && _last is not null && clock.GetUtcNow() - _finishedAt < CacheFor)
                {
                    return _last;
                }

                run = _inFlight ??= Task.Run(RunAsync, CancellationToken.None);
            }

            return await run.WaitAsync(cancellationToken);
        }

        private async Task<Report> RunAsync()
        {
            Report report;

            try
            {
                report = await RunChecksAsync();
            }
            catch (Exception exception)
            {
                // Only a check that could not be built gets here, since every check's own failure is caught.
                // A report still goes out, and the single flight is released: left faulted, it would answer
                // every later caller with this exception.
                report = new Report(
                    HealthStatuses.Unhealthy,
                    [new Service("Health checks", HealthStatuses.Unhealthy, HealthReason.From(exception))]);
            }

            LogChanges(report.Services);

            lock (_gate)
            {
                _last = report;
                _finishedAt = clock.GetUtcNow();
                _inFlight = null;
            }

            return report;
        }

        private async Task<Report> RunChecksAsync()
        {
            var scope = scopes.CreateAsyncScope();
            var bound = new CancellationTokenSource(CheckTimeout, clock);
            var running = new List<Task>();

            try
            {
                var checks = scope.ServiceProvider.GetServices<IServiceHealthCheck>().ToList();

                // Side by side, and started together, so the one deadline is each check's own three seconds
                // and a hung dependency costs the others nothing. Task.Run so a check that blocks before its
                // first await cannot hold up the deadline either.
                var entries = await Task.WhenAll(checks.Select(check =>
                {
                    var task = Task.Run(() => check.CheckAsync(bound.Token), CancellationToken.None);
                    running.Add(task);

                    return ResultOfAsync(check, task, bound.Token);
                }).ToList());

                return new Report(Aggregate(entries), entries.Select(entry => entry.Service).ToList());
            }
            finally
            {
                _ = ReleaseWhenSettledAsync(scope, bound, running);
            }
        }

        private static async Task<Entry> ResultOfAsync(
            IServiceHealthCheck check, Task<ServiceHealthResult> running, CancellationToken bound)
        {
            try
            {
                // WaitAsync as well as the token the check was given: a dependency that hangs in a place that
                // does not watch the token is exactly the case the bound is for.
                var result = await running.WaitAsync(bound);

                return new Entry(check.IsRequired, new Service(check.ServiceName, result.Status, result.Reason));
            }
            catch (Exception exception)
            {
                return new Entry(
                    check.IsRequired,
                    new Service(check.ServiceName, HealthStatuses.Unhealthy, HealthReason.From(exception)));
            }
        }

        /// <summary>
        ///     Ends a run's scope once the last of its checks has really stopped, which can be after the report
        ///     has gone out. Disposing a DbContext under a query that is still being cancelled can hang or
        ///     throw, and neither may reach the report, so this is not awaited.
        /// </summary>
        private static async Task ReleaseWhenSettledAsync(
            AsyncServiceScope scope, CancellationTokenSource bound, List<Task> running)
        {
            await Task.WhenAll(running).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

            bound.Dispose();

            await scope.DisposeAsync();
        }

        private static string Aggregate(IReadOnlyCollection<Entry> entries)
        {
            if (entries.Any(entry => entry.IsRequired && entry.Service.Status != HealthStatuses.Healthy))
            {
                return HealthStatuses.Unhealthy;
            }

            return entries.Any(entry => !entry.IsRequired && IsFailure(entry.Service.Status))
                ? HealthStatuses.Degraded
                : HealthStatuses.Healthy;
        }

        // Once per change of status, and only the reason word: a flood of requests cannot flood the log, and
        // no stack trace is written for a dependency being down.
        private void LogChanges(IEnumerable<Service> services)
        {
            foreach (var service in services)
            {
                _lastStatus.TryGetValue(service.Name, out var previous);
                _lastStatus[service.Name] = service.Status;

                if (service.Status != previous && IsFailure(service.Status))
                {
                    logger.LogWarning("{Service} failed its health check: {Reason}", service.Name, service.Reason);
                }
            }
        }

        private static bool IsFailure(string status) => status is HealthStatuses.Degraded or HealthStatuses.Unhealthy;

        private sealed record Entry(bool IsRequired, Service Service);
    }

    internal sealed record Report(string Status, IReadOnlyList<Service> Services);

    internal sealed record Service(string Name, string Status, string? Reason = null);
}
