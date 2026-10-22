using ArturRios.Heimdall.WebApi.Tests.Support;
using ArturRios.Heimdall.WebApi.Retention;
using ArturRios.Util.Test.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArturRios.Heimdall.WebApi.Tests;

/// <summary>
///     <see cref="ScheduledPassService" />'s loop: what it does with a pass that fails, one that throws,
///     and shutdown (#214, #222). A fake pass on a tiny interval; no database.
/// </summary>
public class ScheduledPassServiceTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private sealed record LogLine(LogLevel Level, string Message, Exception? Exception);

    private sealed class CapturingLogger : ILogger
    {
        private readonly List<LogLine> _lines = [];

        public IReadOnlyList<LogLine> Lines
        {
            get
            {
                lock (_lines)
                {
                    return _lines.ToList();
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_lines)
            {
                _lines.Add(new LogLine(logLevel, formatter(state, exception), exception));
            }
        }
    }

    private sealed class FakeScopeFactory : IServiceScopeFactory
    {
        public int Created;

        public IServiceScope CreateScope()
        {
            Interlocked.Increment(ref Created);

            return new ServiceCollection().BuildServiceProvider().CreateScope();
        }
    }

    private sealed class FakePass(Func<int, ScheduledPassResult> run, CapturingLogger logger, string name)
        : ScheduledPassService(new FakeScopeFactory(), Interval, logger)
    {
        private int _runs;

        public int Runs => Volatile.Read(ref _runs);

        public override string PassName => name;

        protected override string StartupDescription => "fake pass";

        protected override Task<ScheduledPassResult> RunAsync(IServiceProvider scope) =>
            Task.FromResult(run(Interlocked.Increment(ref _runs)));
    }

    private static Task WaitForAsync(Func<bool> condition) =>
        Poll.UntilAsync(condition, "The pass did not reach the awaited state in time.", Patience);

    [UnitFact]
    public async Task GivenAPass_WhenTheIntervalElapses_ThenItRunsRepeatedlyAndLogsASummary()
    {
        var logger = new CapturingLogger();
        var pass = new FakePass(_ => new ScheduledPassResult("did a thing", false, 3), logger, "fake-runs");

        await pass.StartAsync(CancellationToken.None);
        await WaitForAsync(() => pass.Runs >= 3);
        await pass.StopAsync(CancellationToken.None);

        Assert.Contains(logger.Lines, line => line.Level == LogLevel.Information && line.Message == "did a thing");
        Assert.DoesNotContain(logger.Lines, line => line.Level >= LogLevel.Warning);
        Assert.NotNull(ScheduledPassMetrics.LastSuccessOf("fake-runs"));
    }

    [UnitFact]
    public async Task GivenAPassThatReturnsFailure_WhenItRuns_ThenItIsLoggedAtErrorWithTheMarkerAndTheLoopContinues()
    {
        var logger = new CapturingLogger();
        var pass = new FakePass(_ => ScheduledPassResult.Failure("Fake pass", ["boom"]), logger, "fake-failing");

        await pass.StartAsync(CancellationToken.None);
        await WaitForAsync(() => pass.Runs >= 3);
        await pass.StopAsync(CancellationToken.None);

        var failures = logger.Lines.Where(line => line.Level == LogLevel.Error).ToList();

        Assert.True(failures.Count >= 3, "each failed run is logged, and the loop kept running");
        Assert.All(failures, line =>
        {
            Assert.Contains(ScheduledPassService.FailureMarker, line.Message);
            Assert.Contains("fake-failing", line.Message);
            Assert.Contains("boom", line.Message);
        });
        Assert.DoesNotContain(logger.Lines, line => line.Level == LogLevel.Information && line.Message.Contains("boom"));

        // A failed run is never a success: nothing to alert "no success in N hours" against would exist
        Assert.Null(ScheduledPassMetrics.LastSuccessOf("fake-failing"));
    }

    [UnitFact]
    public async Task GivenAPassThatThrows_WhenItRuns_ThenItIsLoggedAtErrorAndTheLoopContinues()
    {
        var logger = new CapturingLogger();
        var pass = new FakePass(
            run => run == 1 ? throw new InvalidOperationException("kaput") : new ScheduledPassResult(null, false),
            logger, "fake-throwing");

        await pass.StartAsync(CancellationToken.None);
        await WaitForAsync(() => pass.Runs >= 3);
        await pass.StopAsync(CancellationToken.None);

        var thrown = Assert.Single(logger.Lines, line => line.Level == LogLevel.Error);

        Assert.IsType<InvalidOperationException>(thrown.Exception);
        Assert.Contains(ScheduledPassService.FailureMarker, thrown.Message);
        Assert.Contains("fake-throwing", thrown.Message);

        // Later runs went through and succeeded
        Assert.NotNull(ScheduledPassMetrics.LastSuccessOf("fake-throwing"));
    }

    [UnitFact]
    public async Task GivenARunningPass_WhenItIsStopped_ThenNoFurtherRunsHappen()
    {
        var logger = new CapturingLogger();
        var pass = new FakePass(_ => new ScheduledPassResult(null, false), logger, "fake-stopping");

        await pass.StartAsync(CancellationToken.None);
        await WaitForAsync(() => pass.Runs >= 1);
        await pass.StopAsync(CancellationToken.None);

        var runsAtStop = pass.Runs;
        await Task.Delay(Interval * 5);

        Assert.Equal(runsAtStop, pass.Runs);
        Assert.DoesNotContain(logger.Lines, line => line.Level >= LogLevel.Warning);
    }

    [UnitFact]
    public async Task GivenAPassThatReportsRows_WhenItRuns_ThenTheCounterIsIncrementedByThatCount()
    {
        var added = 0L;

        using var listener = new System.Diagnostics.Metrics.MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == ScheduledPassMetrics.MeterName)
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
        {
            if (instrument.Name == "heimdall.scheduled_pass.rows_processed" &&
                tags.ToArray().Any(tag => tag.Key == "pass" && (string?)tag.Value == "fake-rows"))
            {
                Interlocked.Add(ref added, measurement);
            }
        });
        listener.Start();

        var logger = new CapturingLogger();
        var pass = new FakePass(run => new ScheduledPassResult("rows", false, 7), logger, "fake-rows");

        await pass.StartAsync(CancellationToken.None);
        await WaitForAsync(() => pass.Runs >= 2);
        await pass.StopAsync(CancellationToken.None);

        var total = Interlocked.Read(ref added);

        Assert.True(total >= 7 && total % 7 == 0, $"each run adds its own count of 7; saw {total}");
    }
}
