using ArturRios.Heimdall.Query.Handlers;
using ArturRios.Heimdall.Query.Input;
using ArturRios.Heimdall.Query.Output;
using ArturRios.Heimdall.Shared.Retention;
using ArturRios.Heimdall.WebApi.Monitoring;
using ArturRios.Heimdall.WebApi.Retention;
using ArturRios.Mediator.Query;
using ArturRios.Mediator.Query.Interfaces;
using ArturRios.Output;
using ArturRios.Util.Test.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArturRios.Heimdall.WebApi.Tests;

/// <summary>
///     What one pass of <see cref="SecurityMonitoringService" /> does with what detection found (NFR-26): each
///     signal is one warning carrying <see cref="SecurityMonitoringService.Marker" />, the line the alerting rule and
///     scripts/security_signals.py match, and a failed detection fails the pass.
/// </summary>
public class SecurityMonitoringServiceTests
{
    private sealed class StubHandler(DataOutput<SecuritySignalsOutput?> result)
        : IQueryHandlerAsync<DetectSecuritySignalsQuery, SecuritySignalsOutput>
    {
        public Task<DataOutput<SecuritySignalsOutput?>> HandleAsync(
            DetectSecuritySignalsQuery query, CancellationToken cancellationToken = default) =>
            Task.FromResult(result);
    }

    private sealed class RecordingLogger : ILogger<SecurityMonitoringService>
    {
        public List<(LogLevel Level, string Text)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    private sealed class Service(IServiceScopeFactory factory, ILogger<SecurityMonitoringService> logger)
        : SecurityMonitoringService(factory, new DataRetentionOptions(), logger)
    {
        public Task<ScheduledPassResult> Run(IServiceProvider scope) => RunAsync(scope);
    }

    private static async Task<(ScheduledPassResult Result, RecordingLogger Logger)> RunAsync(
        DataOutput<SecuritySignalsOutput?> detected)
    {
        var provider = new ServiceCollection()
            .AddScoped<QueryMediator>()
            .AddScoped<IQueryHandlerAsync<DetectSecuritySignalsQuery, SecuritySignalsOutput>>(
                _ => new StubHandler(detected))
            .BuildServiceProvider();
        var logger = new RecordingLogger();

        var result = await new Service(provider.GetRequiredService<IServiceScopeFactory>(), logger).Run(provider);

        return (result, logger);
    }

    private static DataOutput<SecuritySignalsOutput?> Detected(params SecuritySignal[] signals) =>
        DataOutput<SecuritySignalsOutput?>.New.WithData(new SecuritySignalsOutput { Signals = signals });

    [UnitFact]
    public async Task GivenTwoSignals_WhenThePassRuns_ThenEachIsOneMarkedWarningAndThePassIsQuiet()
    {
        // Given one signal about an actor and one about nobody in particular
        var actor = Guid.NewGuid();
        var detected = Detected(
            new SecuritySignal
            {
                Kind = DetectSecuritySignalsQueryHandler.RepeatedRefusals,
                Description = "refused writes by one identity",
                Count = 12,
                ActorId = actor
            },
            new SecuritySignal
            {
                Kind = DetectSecuritySignalsQueryHandler.LockoutSpike,
                Description = "accounts locked out at once",
                Count = 7
            });

        // When
        var (result, logger) = await RunAsync(detected);

        // Then one warning a signal, in the order detected, and nothing more for the scheduler to log
        (LogLevel, string)[] expected =
        [
            (LogLevel.Warning,
                $"SECURITY_SIGNAL REPEATED_REFUSALS: refused writes by one identity (count 12, actor {actor})"),
            (LogLevel.Warning, "SECURITY_SIGNAL LOCKOUT_SPIKE: accounts locked out at once (count 7, actor n/a)")
        ];
        Assert.Equal(expected, logger.Entries);
        Assert.Equal(ScheduledPassResult.Quiet, result);
    }

    [UnitFact]
    public async Task GivenNoSignals_WhenThePassRuns_ThenNothingIsLoggedAndThePassIsQuiet()
    {
        var (result, logger) = await RunAsync(Detected());

        Assert.Empty(logger.Entries);
        Assert.Equal(ScheduledPassResult.Quiet, result);
    }

    [UnitFact]
    public async Task GivenDetectionWithoutData_WhenThePassRuns_ThenNothingIsLoggedAndThePassIsQuiet()
    {
        var (result, logger) = await RunAsync(DataOutput<SecuritySignalsOutput?>.New);

        Assert.Empty(logger.Entries);
        Assert.Equal(ScheduledPassResult.Quiet, result);
    }

    [UnitFact]
    public async Task GivenDetectionFails_WhenThePassRuns_ThenThePassFailsWithItsErrorsAndLogsNoSignal()
    {
        var (result, logger) = await RunAsync(
            DataOutput<SecuritySignalsOutput?>.New.WithError("audit read failed").WithError("timeout"));

        Assert.Empty(logger.Entries);
        Assert.True(result.Failed);
        Assert.Equal("Security signal detection failed: audit read failed; timeout", result.Summary);
        Assert.Equal(0, result.RowsProcessed);
    }
}
