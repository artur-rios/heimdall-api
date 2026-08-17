using ArturRios.Heimdall.Command.Services;
using ArturRios.Util.Test.Attributes;

namespace ArturRios.Heimdall.Command.Tests;

// Unit tests for PasswordHashGate (Threat Model TH-03): the bound on how many Argon2id derivations
// run at once.
//
// The gate is what stands between a caller and unbounded memory demand — 600 MB per derivation, and
// the rate limiter is per IP and per instance, so it bounds one address rather than the aggregate.
//
// Every test builds its own gate and never touches PasswordHashGate.Shared. That is not tidiness:
// when the gate was a static with a reconfigurable semaphore, these tests narrowed the bound to one
// and unrelated handler tests running in parallel were refused by it, while callers already inside
// released permits into a semaphore that had since been replaced. An instance per test has neither
// problem, and Shared keeps serving the rest of the suite at its default.
//
// They exercise the gate through RunAsync rather than through real derivations. What needs proving
// is the counting and the refusal; running Argon2id to demonstrate arithmetic would spend gigabytes
// and tie the suite's duration to the hashing parameters. One test at the end does pay for a real
// derivation, because everything above it would pass against a gate that ran nothing at all.
public class PasswordHashGateTests
{
    private static PasswordHashGate Gate(int maxConcurrent, int maxWaitMilliseconds = 5_000) =>
        new(maxConcurrent, TimeSpan.FromMilliseconds(maxWaitMilliseconds));

    [UnitFact]
    public void GivenTheDefaults_WhenReadingThem_ThenTheyAreSmallEnoughToSurviveTheHashCost()
    {
        // Asserted as numbers rather than against themselves: the value is a memory budget — four
        // derivations at 600 MB is about 2.4 GB — so raising it is a decision about how much RAM the
        // process may commit to hashing, and should have to change this line to happen.
        Assert.Equal(4, PasswordHashGate.DefaultMaxConcurrent);
        Assert.Equal(TimeSpan.FromSeconds(10), PasswordHashGate.DefaultMaxWait);
    }

    [UnitFact]
    public void GivenAnInvalidBound_WhenConstructing_ThenItIsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PasswordHashGate(0, TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PasswordHashGate(-1, TimeSpan.FromSeconds(1)));
    }

    [UnitFact]
    public void GivenAnAbsentOrUnusableSetting_WhenConfiguringShared_ThenItFallsBackRatherThanFailing()
    {
        // A typo in this variable tunes a safety margin. Refusing to boot over one would turn a
        // conservative default into an outage.
        var original = PasswordHashGate.Shared;

        try
        {
            foreach (var value in new[] { null, "", "not-a-number", "0", "-3" })
            {
                Environment.SetEnvironmentVariable(PasswordHashGate.MaxConcurrentVariable, value);

                PasswordHashGate.ConfigureSharedFromEnvironment();

                Assert.Equal(PasswordHashGate.DefaultMaxConcurrent, PasswordHashGate.Shared.MaxConcurrent);
            }

            Environment.SetEnvironmentVariable(PasswordHashGate.MaxConcurrentVariable, "7");

            PasswordHashGate.ConfigureSharedFromEnvironment();

            Assert.Equal(7, PasswordHashGate.Shared.MaxConcurrent);
        }
        finally
        {
            // Restored because this is the one test that touches the shared gate, and every other
            // test in the assembly derives passwords through it.
            Environment.SetEnvironmentVariable(PasswordHashGate.MaxConcurrentVariable, null);
            PasswordHashGate.Shared = original;
        }
    }

    [UnitFact]
    public async Task GivenMoreCallersThanPermits_WhenTheyRun_ThenNoMoreThanTheBoundAreInsideAtOnce()
    {
        // The property the class exists for. Twenty callers, three permits: at no instant may more
        // than three be inside, however they interleave.
        var gate = Gate(3);
        var inFlight = 0;
        var peak = 0;

        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => gate.RunAsync(async () =>
        {
            var now = Interlocked.Increment(ref inFlight);

            // A compare-and-swap loop rather than read-then-write, because two callers racing on the
            // maximum is exactly the interleaving this test would otherwise fail to notice.
            int observed;

            do
            {
                observed = Volatile.Read(ref peak);
            }
            while (now > observed && Interlocked.CompareExchange(ref peak, now, observed) != observed);

            // Yields inside the gate, so the callers genuinely overlap rather than each finishing
            // before the next is scheduled.
            await Task.Delay(20);

            Interlocked.Decrement(ref inFlight);
        })));

        Assert.True(peak <= 3, $"{peak} ran at once against a bound of 3");

        // Without this the test would pass on a machine that never overlapped them, which would make
        // it evidence of nothing.
        Assert.True(peak > 1, "the callers never overlapped, so the bound was never exercised");
    }

    [UnitFact]
    public async Task GivenEveryPermitHeld_WhenAnotherCallerArrives_ThenItIsRefusedRatherThanQueuedForever()
    {
        // The other half of the protection. An unbounded queue is the condition the gate exists to
        // prevent, moved out of memory and into the thread pool.
        var gate = Gate(1, maxWaitMilliseconds: 50);
        var release = new TaskCompletionSource();
        var holding = new TaskCompletionSource();

        var holder = gate.RunAsync(async () =>
        {
            holding.SetResult();

            await release.Task;
        });

        await holding.Task;

        await Assert.ThrowsAsync<PasswordHashGateSaturatedException>(
            () => gate.RunAsync(() => Task.CompletedTask));

        release.SetResult();
        await holder;

        // Then — and it accepts again once the work ahead finishes, so saturation is a load
        // condition rather than a latch that something has to reset.
        await gate.RunAsync(() => Task.CompletedTask);
    }

    [UnitFact]
    public async Task GivenACallerThrewInsideTheGate_WhenTheNextArrives_ThenThePermitWasReturned()
    {
        // A leaked permit closes the gate a little further every time until the protection is the
        // outage — and it would leak silently, since the failing request has its own error to report.
        var gate = Gate(1, maxWaitMilliseconds: 50);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => gate.RunAsync<int>(() => throw new InvalidOperationException("derivation failed")));

        await gate.RunAsync(() => Task.CompletedTask);
    }

    [UnitFact]
    public async Task GivenARealDerivation_WhenRunThroughTheGate_ThenItStillHashesAndVerifies()
    {
        // The control for everything above: a gate that ran nothing at all would satisfy each of
        // those tests.
        var gate = Gate(2);

        var (hash, salt) = await gate.EncodeWithRandomSaltAsync("Str0ng-Gate-Pass!");

        Assert.NotEmpty(hash);
        Assert.NotEmpty(salt);

        Assert.True(await gate.TextMatchesAsync("Str0ng-Gate-Pass!", hash, salt));
        Assert.False(await gate.TextMatchesAsync("wrong-password", hash, salt));
    }
}
