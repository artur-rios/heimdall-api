using ArturRios.Heimdall.WebApi.Monitoring;
using ArturRios.Util.Test.Attributes;

namespace ArturRios.Heimdall.WebApi.Tests;

/// <summary>
///     The readiness answer is held for a few seconds, so an anonymous caller cannot turn the probe into a
///     database read per request (#215).
/// </summary>
public class ReadinessCacheTests
{
    [UnitFact]
    public async Task GivenTwoCallsWithinTheWindow_WhenAsked_ThenTheDatabaseIsReadOnce()
    {
        // Given
        var clock = new ManualTimeProvider();
        var cache = new ReadinessCache(clock);
        var reads = 0;
        Task<bool> Check() { reads++; return Task.FromResult(false); }

        // When
        var first = await cache.IsReadyAsync(Check);
        clock.Advance(ReadinessCache.Window - TimeSpan.FromMilliseconds(1));
        var second = await cache.IsReadyAsync(Check);

        // Then the unhealthy answer is shared, with no second read
        Assert.False(first);
        Assert.False(second);
        Assert.Equal(1, reads);
    }

    [UnitFact]
    public async Task GivenACallAfterTheWindow_WhenAsked_ThenTheDatabaseIsReadAgain()
    {
        // Given
        var clock = new ManualTimeProvider();
        var cache = new ReadinessCache(clock);
        var healthy = false;
        var reads = 0;
        Task<bool> Check() { reads++; return Task.FromResult(healthy); }
        await cache.IsReadyAsync(Check);
        healthy = true;

        // When
        clock.Advance(ReadinessCache.Window);
        var result = await cache.IsReadyAsync(Check);

        // Then
        Assert.True(result);
        Assert.Equal(2, reads);
    }

    [UnitFact]
    public async Task GivenManyConcurrentCallsWhenTheWindowHasExpired_WhenAsked_ThenOneReadServesThemAll()
    {
        // Given a read that stays in flight until released
        var cache = new ReadinessCache(new ManualTimeProvider());
        var release = new TaskCompletionSource<bool>();
        var reads = 0;
        Task<bool> Check() { Interlocked.Increment(ref reads); return release.Task; }

        // When twenty callers arrive while it is in flight
        var calls = Enumerable.Range(0, 20).Select(_ => cache.IsReadyAsync(Check)).ToArray();
        release.SetResult(true);
        var results = await Task.WhenAll(calls);

        // Then
        Assert.All(results, Assert.True);
        Assert.Equal(1, reads);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
