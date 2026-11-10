using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Query.HealthChecks;
using ArturRios.Heimdall.WebApi.Email;
using ArturRios.Heimdall.WebApi.Metrics;
using ArturRios.Heimdall.WebApi.Monitoring;
using ArturRios.Output;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Mock;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace ArturRios.Heimdall.WebApi.Tests;

// The report on the metrics port, and the port itself. The in-memory server has no sockets, so these
// hosts are built from the calls Startup makes, with one middleware in front that stamps the local port
// a real Kestrel connection would have carried, and a terminal step standing in for the rest of the API.
// That step marks its 404, which is how a test tells "the port refused it" from "the API did".
public class InternalHealthTests
{
    private const int MetricsPort = MetricsOptions.DefaultPort;
    private const int ApplicationPort = 8080;
    private const string Route = "/internal/health";
    private const string ReachedTheApi = "X-Reached-The-Api";
    private const string HermesAddress = "http://hermes-api.test:8080";
    private const string HermesKey = "hermes-key-that-must-not-be-sent";
    private const string Secret = "secret-detail-that-must-not-leak";

    private static readonly TimeSpan Soon = TimeSpan.FromSeconds(30);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [UnitFact]
    public async Task GivenAllHealthy_WhenRequestedOnMetricsPort_Then200Healthy()
    {
        // Given
        await using var app = await StartAsync(MetricsPort, RealChecks(new AsyncFakeRepository<Role, long>(), Hermes.Answering()));

        // When
        var response = await app.GetTestClient().GetAsync(Route, Token);
        var body = await response.Content.ReadAsStringAsync(Token);

        // Then the bare shape, with no DataOutput envelope around it
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(
            """{"status":"Healthy","services":[{"name":"Database","status":"Healthy"},{"name":"Hermes","status":"Healthy"}]}""",
            body);
    }

    [UnitFact]
    public async Task GivenHermesDown_Then200DegradedWithReason()
    {
        // Given
        await using var app = await StartAsync(MetricsPort, RealChecks(new AsyncFakeRepository<Role, long>(), Hermes.Refusing()));

        // When
        var response = await app.GetTestClient().GetAsync(Route, Token);
        var body = await response.Content.ReadAsStringAsync(Token);

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            """{"status":"Degraded","services":[{"name":"Database","status":"Healthy"},{"name":"Hermes","status":"Unhealthy","reason":"connection refused"}]}""",
            body);
    }

    [UnitTheory]
    [InlineData(HttpStatusCode.InternalServerError, "HTTP 500")]
    [InlineData(HttpStatusCode.Found, "HTTP 302")]
    public async Task GivenHermesAnswersOutsideTwoHundreds_WhenRequested_ThenDegradedWithTheCode(HttpStatusCode status, string reason)
    {
        // Given
        await using var app = await StartAsync(
            MetricsPort, RealChecks(new AsyncFakeRepository<Role, long>(), Hermes.Answering(status)));

        // When
        var response = await app.GetTestClient().GetAsync(Route, Token);
        var body = await response.Content.ReadAsStringAsync(Token);

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains($$"""{"name":"Hermes","status":"Unhealthy","reason":"{{reason}}"}""", body);
        Assert.StartsWith("""{"status":"Degraded",""", body);
    }

    [UnitFact]
    public async Task GivenDatabaseDown_Then503Unhealthy()
    {
        // Given a read that fails with text the report must not repeat
        await using var app = await StartAsync(MetricsPort, RealChecks(new ThrowingRoleRepository(), Hermes.Answering()));

        // When
        var response = await app.GetTestClient().GetAsync(Route, Token);
        var body = await response.Content.ReadAsStringAsync(Token);

        // Then the same body as ever, under a 503
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(
            """{"status":"Unhealthy","services":[{"name":"Database","status":"Unhealthy","reason":"request failed"},{"name":"Hermes","status":"Healthy"}]}""",
            body);
    }

    [UnitTheory]
    [InlineData("", HermesKey)]
    [InlineData(HermesAddress, "")]
    [InlineData("not an address", HermesKey)]
    public async Task GivenHermesNotConfigured_ThenNotConfiguredAndNoCallMade(string address, string key)
    {
        // Given
        var hermes = Hermes.Answering();
        await using var app = await StartAsync(
            MetricsPort, RealChecks(new AsyncFakeRepository<Role, long>(), hermes, address, key));

        // When
        var response = await app.GetTestClient().GetAsync(Route, Token);
        var body = await response.Content.ReadAsStringAsync(Token);

        // Then not configured does not count against the report
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            """{"status":"Healthy","services":[{"name":"Database","status":"Healthy"},{"name":"Hermes","status":"NotConfigured"}]}""",
            body);
        Assert.Empty(hermes.Requests);
    }

    [UnitTheory]
    [InlineData(HermesAddress)]
    [InlineData(HermesAddress + "/")]
    public async Task GivenHermesConfigured_WhenChecked_ThenOneBareGetWithoutTheKey(string address)
    {
        // Given
        var hermes = Hermes.Answering();
        await using var app = await StartAsync(
            MetricsPort, RealChecks(new AsyncFakeRepository<Role, long>(), hermes, address));

        // When
        await app.GetTestClient().GetAsync(Route, Token);

        // Then the public address, with nothing that could carry the credential
        var request = Assert.Single(hermes.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal($"{HermesAddress}/healthcheck", request.Uri);
        Assert.Null(request.Authorization);
        Assert.DoesNotContain(HermesKey, request.Headers);
        Assert.False(request.HasBody);
    }

    [UnitFact]
    public void GivenTheHermesClient_WhenBuilt_ThenItDoesNotFollowRedirects()
    {
        // Given the registration, with the real primary handler
        var services = new ServiceCollection();
        services.AddHealthProbes();
        using var provider = services.BuildServiceProvider();

        // When
        var handler = provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(HermesHealthCheck.ClientName);

        while (handler is DelegatingHandler delegating)
        {
            handler = delegating.InnerHandler!;
        }

        // Then a redirect cannot send the check to a host nobody configured
        Assert.False(Assert.IsType<SocketsHttpHandler>(handler).AllowAutoRedirect);
    }

    [UnitFact]
    public async Task GivenApplicationPort_WhenInternalHealthRequested_Then404()
    {
        // Given
        var hermes = Hermes.Answering();
        await using var app = await StartAsync(ApplicationPort, RealChecks(new AsyncFakeRepository<Role, long>(), hermes));

        // When
        var response = await app.GetTestClient().GetAsync(Route, Token);

        // Then it is just another address the API does not have, and no check ran
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.True(response.Headers.Contains(ReachedTheApi));
        Assert.Empty(hermes.Requests);
    }

    [UnitTheory]
    [InlineData("/api/auth/login")]
    [InlineData("/healthcheck")]
    [InlineData("/healthcheck/ready")]
    [InlineData("/healthcheck/detailed")]
    [InlineData("/api/healthcheck/detailed")]
    [InlineData("/.well-known/jwks.json")]
    [InlineData("/internal")]
    [InlineData("/internal/health/")]
    [InlineData("/internal/health/extra")]
    [InlineData("/")]
    public async Task GivenAnyOtherPathOnMetricsPort_Then404(string path)
    {
        // Given
        await using var app = await StartAsync(MetricsPort, Stubs());

        // When
        var response = await app.GetTestClient().GetAsync(path, Token);

        // Then the port refused it, before anything of the API ran
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.False(response.Headers.Contains(ReachedTheApi));
    }

    [UnitTheory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    [InlineData("HEAD")]
    public async Task GivenAnotherMethodOnMetricsPort_WhenInternalHealthRequested_Then404(string method)
    {
        // Given
        var check = StubCheck.Returning("Database", required: true, HealthStatuses.Healthy);
        await using var app = await StartAsync(MetricsPort, Stubs(check));

        // When
        var response = await app.GetTestClient().SendAsync(new HttpRequestMessage(new HttpMethod(method), Route), Token);

        // Then
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.False(response.Headers.Contains(ReachedTheApi));
        Assert.Equal(0, check.Runs);
    }

    [UnitFact]
    public async Task GivenMetricsPort_WhenScraped_ThenStillServed()
    {
        // Given
        await using var app = await StartAsync(MetricsPort, Stubs());

        // When
        var response = await app.GetTestClient().GetAsync("/metrics", Token);
        var body = await response.Content.ReadAsStringAsync(Token);

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains($"service_name=\"{MetricsConfiguration.ServiceName}\"", body);
    }

    [UnitTheory]
    [InlineData(Route)]
    [InlineData("/api/auth/login")]
    public async Task GivenMetricsSwitchedOff_WhenRequestedOnThatPort_ThenNothingIsTakenFromTheApi(string path)
    {
        // Given metrics off, so that port is no different from any other
        var check = StubCheck.Returning("Database", required: true, HealthStatuses.Healthy);
        await using var app = await StartAsync(MetricsPort, Stubs(check), MetricsOptions.Parse("0"));

        // When
        var response = await app.GetTestClient().GetAsync(path, Token);

        // Then
        Assert.True(response.Headers.Contains(ReachedTheApi));
        Assert.Equal(0, check.Runs);
    }

    [UnitFact]
    public async Task GivenMetricsSwitchedOff_WhenLocalPortIsZero_ThenThereIsNoInternalRoute()
    {
        // Port 0 is "off", and 0 is also the local port an in-memory host reports.
        var check = StubCheck.Returning("Database", required: true, HealthStatuses.Healthy);
        await using var app = await StartAsync(0, Stubs(check), MetricsOptions.Parse("0"));

        var response = await app.GetTestClient().GetAsync(Route, Token);

        Assert.True(response.Headers.Contains(ReachedTheApi));
        Assert.Equal(0, check.Runs);
    }

    [UnitTheory]
    [InlineData(HealthStatuses.Healthy, HealthStatuses.Healthy, HttpStatusCode.OK, HealthStatuses.Healthy)]
    [InlineData(HealthStatuses.Healthy, HealthStatuses.NotConfigured, HttpStatusCode.OK, HealthStatuses.Healthy)]
    [InlineData(HealthStatuses.Healthy, HealthStatuses.Degraded, HttpStatusCode.OK, HealthStatuses.Degraded)]
    [InlineData(HealthStatuses.Healthy, HealthStatuses.Unhealthy, HttpStatusCode.OK, HealthStatuses.Degraded)]
    [InlineData(HealthStatuses.Unhealthy, HealthStatuses.Healthy, HttpStatusCode.ServiceUnavailable, HealthStatuses.Unhealthy)]
    [InlineData(HealthStatuses.Degraded, HealthStatuses.Healthy, HttpStatusCode.ServiceUnavailable, HealthStatuses.Unhealthy)]
    [InlineData(HealthStatuses.NotConfigured, HealthStatuses.Healthy, HttpStatusCode.ServiceUnavailable, HealthStatuses.Unhealthy)]
    [InlineData(HealthStatuses.Unhealthy, HealthStatuses.Degraded, HttpStatusCode.ServiceUnavailable, HealthStatuses.Unhealthy)]
    public async Task GivenCheckStatuses_WhenRequested_ThenTheAggregateFollowsTheRule(
        string required, string optional, HttpStatusCode expectedCode, string expectedStatus)
    {
        // Given
        await using var app = await StartAsync(MetricsPort, Stubs(
            StubCheck.Returning("Required", required: true, required),
            StubCheck.Returning("Optional", required: false, optional)));

        // When
        var response = await app.GetTestClient().GetAsync(Route, Token);
        var body = await response.Content.ReadAsStringAsync(Token);

        // Then
        Assert.Equal(expectedCode, response.StatusCode);
        Assert.StartsWith($$"""{"status":"{{expectedStatus}}",""", body);
    }

    [UnitFact]
    public async Task GivenAReport_WhenRequestedWithinFiveSecondsOfFinishing_ThenTheChecksDoNotRunAgain()
    {
        // Given a check that takes two seconds of the clock, so finishing and starting are told apart
        var clock = new ManualClock();
        var check = new StubCheck("Database", required: true, _ =>
        {
            clock.Advance(TimeSpan.FromSeconds(2));

            return Task.FromResult(new ServiceHealthResult(HealthStatuses.Healthy));
        });
        await using var app = await StartAsync(MetricsPort, Stubs(check), clock: clock);
        var client = app.GetTestClient();
        await client.GetAsync(Route, Token);

        // When just inside five seconds of the end, which is past five from the start
        clock.Advance(InternalHealthEndpoint.CacheFor - TimeSpan.FromMilliseconds(1));
        await client.GetAsync(Route, Token);

        // Then
        Assert.Equal(1, check.Runs);
    }

    [UnitFact]
    public async Task GivenAReport_WhenRequestedFiveSecondsAfterFinishing_ThenTheChecksRunAgain()
    {
        // Given
        var clock = new ManualClock();
        var check = StubCheck.Returning("Database", required: true, HealthStatuses.Healthy);
        await using var app = await StartAsync(MetricsPort, Stubs(check), clock: clock);
        var client = app.GetTestClient();
        await client.GetAsync(Route, Token);

        // When
        clock.Advance(InternalHealthEndpoint.CacheFor);
        await client.GetAsync(Route, Token);

        // Then
        Assert.Equal(2, check.Runs);
    }

    [UnitFact]
    public async Task GivenAFailedReport_WhenRequestedWithinTheWindow_ThenItIsCachedToo()
    {
        // Given
        var check = StubCheck.Throwing("Database", required: true, new InvalidOperationException(Secret));
        await using var app = await StartAsync(MetricsPort, Stubs(check), clock: new ManualClock());
        var client = app.GetTestClient();

        // When
        var first = await client.GetAsync(Route, Token);
        var second = await client.GetAsync(Route, Token);

        // Then
        Assert.Equal(HttpStatusCode.ServiceUnavailable, first.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, second.StatusCode);
        Assert.Equal(1, check.Runs);
    }

    [UnitFact]
    public async Task GivenChecksInFlight_WhenAnotherRequestArrives_ThenItSharesTheRun()
    {
        // Given a run held open
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var check = StubCheck.Gated("Database", required: true, gate);
        await using var app = await StartAsync(MetricsPort, Stubs(check));
        var client = app.GetTestClient();

        var first = client.GetAsync(Route, Token);
        await check.Started.Task.WaitAsync(Soon, Token);

        // When
        var second = client.GetAsync(Route, Token);
        await Task.Delay(100, Token);
        gate.SetResult();
        var responses = await Task.WhenAll(first, second).WaitAsync(Soon, Token);

        // Then
        Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
        Assert.Equal(1, check.Runs);
    }

    [UnitFact]
    public async Task GivenACallerGivesUp_WhenChecksAreInFlight_ThenTheRunGoesOnForTheOthers()
    {
        // Given a run started by a caller who then leaves
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var check = StubCheck.Gated("Database", required: true, gate);
        await using var app = await StartAsync(MetricsPort, Stubs(check));
        var client = app.GetTestClient();
        using var leaving = new CancellationTokenSource();

        var abandoned = client.GetAsync(Route, leaving.Token);
        await check.Started.Task.WaitAsync(Soon, Token);
        await leaving.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned.WaitAsync(Soon, Token));

        // When another caller waits on it
        var staying = client.GetAsync(Route, Token);
        await Task.Delay(100, Token);
        Assert.False(check.CancellationSeen);
        gate.SetResult();
        var response = await staying.WaitAsync(Soon, Token);

        // Then the one run answered them, uncancelled
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, check.Runs);
    }

    [UnitFact]
    public async Task GivenAHungCheckThatIgnoresCancellation_WhenTheBoundPasses_ThenTheReportSaysTimeout()
    {
        // Given Hermes never answering and not listening for cancellation, beside a healthy database
        var clock = new ManualClock();
        var never = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hung = StubCheck.Gated("Hermes", required: false, never);
        var database = StubCheck.Returning("Database", required: true, HealthStatuses.Healthy);
        await using var app = await StartAsync(MetricsPort, Stubs(database, hung), clock: clock);

        var request = app.GetTestClient().GetAsync(Route, Token);
        await hung.Started.Task.WaitAsync(Soon, Token);

        // When
        clock.Advance(InternalHealthEndpoint.CheckTimeout);
        var response = await request.WaitAsync(Soon, Token);
        var body = await response.Content.ReadAsStringAsync(Token);
        never.SetResult();

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            """{"status":"Degraded","services":[{"name":"Database","status":"Healthy"},{"name":"Hermes","status":"Unhealthy","reason":"timeout"}]}""",
            body);
    }

    [UnitFact]
    public async Task GivenAHungCheck_WhenItsBoundPasses_ThenItsScopeIsKeptUntilItSettles()
    {
        // Given a check still running after the report has gone out: disposing what it is using then
        // could hang the run, or throw into it.
        var clock = new ManualClock();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hung = StubCheck.Gated("Hermes", required: false, release);
        await using var app = await StartAsync(MetricsPort, Stubs(hung), clock: clock);

        var request = app.GetTestClient().GetAsync(Route, Token);
        await hung.Started.Task.WaitAsync(Soon, Token);
        clock.Advance(InternalHealthEndpoint.CheckTimeout);
        await request.WaitAsync(Soon, Token);

        Assert.False(hung.Disposed.Task.IsCompleted);

        // When
        release.SetResult();

        // Then
        await hung.Disposed.Task.WaitAsync(Soon, Token);
    }

    [UnitFact]
    public async Task GivenScopedChecks_WhenEachRunStarts_ThenItGetsItsOwnScope()
    {
        // Given
        var clock = new ManualClock();
        var created = new List<StubCheck>();
        await using var app = await StartAsync(MetricsPort, services =>
        {
            services.RemoveAll<IServiceHealthCheck>();
            services.AddScoped<IServiceHealthCheck>(_ =>
            {
                var check = StubCheck.Returning("Database", required: true, HealthStatuses.Healthy);
                created.Add(check);

                return check;
            });
        }, clock: clock);
        var client = app.GetTestClient();

        // When
        await client.GetAsync(Route, Token);
        clock.Advance(InternalHealthEndpoint.CacheFor);
        await client.GetAsync(Route, Token);

        // Then one instance per run, each disposed with its scope
        Assert.Equal(2, created.Count);
        await Task.WhenAll(created.Select(check => check.Disposed.Task)).WaitAsync(Soon, Token);
    }

    [UnitFact]
    public async Task GivenACheckThatCannotBeBuilt_WhenRequested_Then503AndTheNextRunIsNotStuck()
    {
        // Given a registration that throws, with text the report must not repeat
        var clock = new ManualClock();
        var attempts = 0;
        await using var app = await StartAsync(MetricsPort, services =>
        {
            services.RemoveAll<IServiceHealthCheck>();
            services.AddScoped<IServiceHealthCheck>(_ =>
            {
                Interlocked.Increment(ref attempts);

                throw new InvalidOperationException(Secret);
            });
        }, clock: clock);
        var client = app.GetTestClient();

        // When
        var first = await client.GetAsync(Route, Token).WaitAsync(Soon, Token);
        var body = await first.Content.ReadAsStringAsync(Token);
        clock.Advance(InternalHealthEndpoint.CacheFor);
        var second = await client.GetAsync(Route, Token).WaitAsync(Soon, Token);

        // Then
        Assert.Equal(HttpStatusCode.ServiceUnavailable, first.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, second.StatusCode);
        Assert.DoesNotContain(Secret, body);
        Assert.Contains("\"reason\":\"request failed\"", body);
        Assert.Equal(2, attempts);
    }

    [UnitFact]
    public async Task GivenACheckFailing_WhenRunRepeatedly_ThenItIsLoggedOncePerStatusChange()
    {
        // Given outcomes that stay failed, recover, then fail again
        var clock = new ManualClock();
        var logs = new RecordingLogs();
        var outcomes = new Queue<Func<ServiceHealthResult>>([
            () => throw new HttpRequestException(Secret, new SocketException((int)SocketError.ConnectionRefused)),
            () => throw new HttpRequestException(Secret, new SocketException((int)SocketError.ConnectionRefused)),
            () => throw new TimeoutException(Secret),
            () => new ServiceHealthResult(HealthStatuses.Healthy),
            () => new ServiceHealthResult(HealthStatuses.Unhealthy, "HTTP 500")
        ]);
        var check = new StubCheck("Hermes", required: false, _ => Task.FromResult(outcomes.Dequeue()()));
        await using var app = await StartAsync(MetricsPort, Stubs(check), clock: clock, logs: logs);
        var client = app.GetTestClient();

        // When
        for (var run = 0; run < 5; run++)
        {
            await client.GetAsync(Route, Token);
            clock.Advance(InternalHealthEndpoint.CacheFor);
        }

        // Then twice, with the reason word alone: no exception, so no stack trace
        Assert.Equal(5, check.Runs);
        Assert.Collection(
            logs.Entries,
            entry => Assert.Contains("connection refused", entry.Message),
            entry => Assert.Contains("HTTP 500", entry.Message));
        Assert.All(logs.Entries, entry =>
        {
            Assert.Equal(LogLevel.Warning, entry.Level);
            Assert.Null(entry.Exception);
            Assert.DoesNotContain(Secret, entry.Message);
        });
    }

    [UnitFact]
    public async Task GivenHealthyAndNotConfiguredChecks_WhenRun_ThenNothingIsLogged()
    {
        // Given
        var logs = new RecordingLogs();
        await using var app = await StartAsync(MetricsPort, Stubs(
            StubCheck.Returning("Database", required: true, HealthStatuses.Healthy),
            StubCheck.Returning("Hermes", required: false, HealthStatuses.NotConfigured)), logs: logs);

        // When
        await app.GetTestClient().GetAsync(Route, Token);

        // Then
        Assert.Empty(logs.Entries);
    }

    [UnitFact]
    public void GivenAnUnknownException_WhenDescribed_ThenRequestFailedWithoutItsText() =>
        Assert.Equal("request failed", HealthReason.From(new InvalidOperationException(Secret)));

    [UnitFact]
    public void GivenCancellation_WhenDescribed_ThenTimeout() =>
        Assert.Equal("timeout", HealthReason.From(new TaskCanceledException(Secret)));

    [UnitFact]
    public void GivenATimeout_WhenDescribed_ThenTimeout() =>
        Assert.Equal("timeout", HealthReason.From(new TimeoutException(Secret)));

    [UnitTheory]
    [InlineData(SocketError.ConnectionRefused, "connection refused")]
    [InlineData(SocketError.HostNotFound, "name not resolved")]
    [InlineData(SocketError.TryAgain, "name not resolved")]
    [InlineData(SocketError.NoData, "name not resolved")]
    [InlineData(SocketError.HostUnreachable, "host unreachable")]
    [InlineData(SocketError.NetworkUnreachable, "host unreachable")]
    [InlineData(SocketError.ConnectionReset, "connection reset")]
    [InlineData(SocketError.TimedOut, "timeout")]
    [InlineData(SocketError.AccessDenied, "connection failed")]
    public void GivenASocketError_WhenDescribed_ThenItsWord(SocketError error, string expected) =>
        Assert.Equal(expected, HealthReason.From(new HttpRequestException(Secret, new SocketException((int)error))));

    [UnitTheory]
    [InlineData(HttpRequestError.NameResolutionError, "name not resolved")]
    [InlineData(HttpRequestError.ConnectionError, "connection failed")]
    [InlineData(HttpRequestError.SecureConnectionError, "tls error")]
    [InlineData(HttpRequestError.ResponseEnded, "connection closed")]
    [InlineData(HttpRequestError.InvalidResponse, "invalid response")]
    [InlineData(HttpRequestError.Unknown, "request failed")]
    public void GivenARequestError_WhenDescribed_ThenItsWord(HttpRequestError error, string expected) =>
        Assert.Equal(expected, HealthReason.From(new HttpRequestException(error, Secret)));

    [UnitFact]
    public void GivenADatabaseDriverFailure_WhenDescribed_ThenTheCauseInsideItIsNamed()
    {
        // The driver and EF Core each wrap the socket's error in an exception of their own.
        var refused = new Exception(Secret, new SocketException((int)SocketError.ConnectionRefused));
        var timedOut = new Exception(Secret, new TimeoutException(Secret));
        var tls = new Exception(Secret, new AuthenticationException(Secret));

        Assert.Equal("connection refused", HealthReason.From(new InvalidOperationException(Secret, refused)));
        Assert.Equal("timeout", HealthReason.From(new InvalidOperationException(Secret, timedOut)));
        Assert.Equal("tls error", HealthReason.From(new InvalidOperationException(Secret, tls)));
    }

    private static async Task<WebApplication> StartAsync(
        int localPort,
        Action<IServiceCollection> configure,
        MetricsOptions? options = null,
        ManualClock? clock = null,
        RecordingLogs? logs = null)
    {
        var metrics = options ?? MetricsOptions.Parse(null);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        if (logs is not null)
        {
            builder.Logging.AddProvider(logs);
        }

        builder.Services.AddHeimdallMetrics(metrics);
        builder.Services.AddHealthProbes();
        configure(builder.Services);

        if (clock is not null)
        {
            builder.Services.AddSingleton<TimeProvider>(clock);
        }

        var app = builder.Build();

        app.Use((context, next) =>
        {
            context.Connection.LocalPort = localPort;

            return next(context);
        });
        app.UseHeimdallMetrics(metrics);
        app.Run(context =>
        {
            context.Response.Headers[ReachedTheApi] = "true";
            context.Response.StatusCode = StatusCodes.Status404NotFound;

            return Task.CompletedTask;
        });

        await app.StartAsync();

        return app;
    }

    // The registered checks, with only their dependencies replaced.
    private static Action<IServiceCollection> RealChecks(
        IAsyncReadOnlyRepository<Role, long> database, Hermes hermes, string address = HermesAddress, string key = HermesKey) =>
        services =>
        {
            services.AddScoped(_ => database);
            services.AddSingleton(new EmailDeliveryOptions { HermesBaseUrl = address, HermesApiKey = key });
            services.AddHttpClient(HermesHealthCheck.ClientName).ConfigurePrimaryHttpMessageHandler(() => hermes);
        };

    private static Action<IServiceCollection> Stubs(params StubCheck[] checks) =>
        services =>
        {
            services.RemoveAll<IServiceHealthCheck>();

            foreach (var check in checks)
            {
                services.AddScoped<IServiceHealthCheck>(_ => check);
            }
        };

    private sealed class StubCheck(string name, bool required, Func<CancellationToken, Task<ServiceHealthResult>> run)
        : IServiceHealthCheck, IDisposable
    {
        private int _runs;
        private CancellationToken _token;

        public int Runs => _runs;

        public bool CancellationSeen => _token.IsCancellationRequested;

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string ServiceName => name;

        public bool IsRequired => required;

        public static StubCheck Returning(string name, bool required, string status) =>
            new(name, required, _ => Task.FromResult(new ServiceHealthResult(status)));

        public static StubCheck Throwing(string name, bool required, Exception exception) =>
            new(name, required, _ => Task.FromException<ServiceHealthResult>(exception));

        // Deliberately does not watch the token: a dependency that hangs is not obliged to cooperate.
        public static StubCheck Gated(string name, bool required, TaskCompletionSource gate) =>
            new(name, required, async _ =>
            {
                await gate.Task;

                return new ServiceHealthResult(HealthStatuses.Healthy);
            });

        public Task<ServiceHealthResult> CheckAsync(CancellationToken cancellationToken)
        {
            _token = cancellationToken;
            Interlocked.Increment(ref _runs);
            Started.TrySetResult();

            return run(cancellationToken);
        }

        public void Dispose() => Disposed.TrySetResult();
    }

    private sealed class ThrowingRoleRepository : IAsyncReadOnlyRepository<Role, long>
    {
        public IQueryable<Role> Query() => throw new InvalidOperationException(Secret);

        public Task<DataOutput<IEnumerable<Role>>> GetAllAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(Secret);

        public Task<DataOutput<Role?>> GetByIdAsync(long id, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(Secret);
    }

    // Stands in for Hermes behind the check's own HttpClient, and keeps what the check sent.
    private sealed class Hermes : HttpMessageHandler
    {
        private readonly Func<HttpResponseMessage> _answer;

        private Hermes(Func<HttpResponseMessage> answer) => _answer = answer;

        public ConcurrentQueue<Captured> Requests { get; } = new();

        public static Hermes Answering(HttpStatusCode status = HttpStatusCode.OK) =>
            new(() => new HttpResponseMessage(status));

        public static Hermes Refusing() =>
            new(() => throw new HttpRequestException(Secret, new SocketException((int)SocketError.ConnectionRefused)));

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Enqueue(new Captured(
                request.Method,
                request.RequestUri?.ToString(),
                request.Headers.Authorization,
                request.Headers.ToString(),
                request.Content is not null));

            return Task.FromResult(_answer());
        }
    }

    private sealed record Captured(
        HttpMethod Method,
        string? Uri,
        System.Net.Http.Headers.AuthenticationHeaderValue? Authorization,
        string Headers,
        bool HasBody);

    private sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

    // Keeps only what the report's own class writes: the host logs too, and those lines are not under test.
    private sealed class RecordingLogs : ILoggerProvider
    {
        private readonly ConcurrentQueue<LogEntry> _entries = new();

        public IReadOnlyCollection<LogEntry> Entries => _entries;

        public ILogger CreateLogger(string categoryName) =>
            categoryName.Contains(nameof(InternalHealthEndpoint), StringComparison.Ordinal)
                ? new Recorder(_entries)
                : Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

        public void Dispose()
        {
        }

        private sealed class Recorder(ConcurrentQueue<LogEntry> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                entries.Enqueue(new LogEntry(logLevel, formatter(state, exception), exception));
        }
    }

    // A clock whose timers fire when a test moves it, so a three-second bound costs no three seconds.
    private sealed class ManualClock : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<ManualTimer> _timers = [];
        private DateTimeOffset _now = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow()
        {
            lock (_gate)
            {
                return _now;
            }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (_gate)
            {
                var timer = new ManualTimer(this, () => callback(state), _now + dueTime);
                _timers.Add(timer);

                return timer;
            }
        }

        public void Advance(TimeSpan by)
        {
            List<ManualTimer> due;

            lock (_gate)
            {
                _now += by;
                due = _timers.Where(timer => timer.DueAt <= _now).ToList();
                _timers.RemoveAll(due.Contains);
            }

            foreach (var timer in due)
            {
                timer.Fire();
            }
        }

        private void Remove(ManualTimer timer)
        {
            lock (_gate)
            {
                _timers.Remove(timer);
            }
        }

        private sealed class ManualTimer(ManualClock clock, Action fire, DateTimeOffset dueAt) : ITimer
        {
            public DateTimeOffset DueAt { get; } = dueAt;

            public void Fire() => fire();

            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose() => clock.Remove(this);

            public ValueTask DisposeAsync()
            {
                Dispose();

                return ValueTask.CompletedTask;
            }
        }
    }
}
