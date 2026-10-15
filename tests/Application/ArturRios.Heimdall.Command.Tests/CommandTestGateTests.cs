using ArturRios.Heimdall.Command.Services;
using ArturRios.Util.Test.Attributes;

namespace ArturRios.Heimdall.Command.Tests;

// Proves CommandTestGate ran before the first test: the Shared gate this assembly derives through
// must wait long enough for a derivation queued behind a loaded machine, not the production ten
// seconds.
public class CommandTestGateTests
{
    [UnitFact]
    public void GivenTheTestRun_WhenReadingTheSharedGate_ThenItWaitsAtLeastAMinuteWithTheProductionPermits()
    {
        // Given
        var shared = PasswordHashGate.Shared;

        // When
        var wait = shared.MaxWait;

        // Then
        Assert.True(wait >= TimeSpan.FromMinutes(1), $"the shared gate waits only {wait}");
        Assert.Equal(PasswordHashGate.DefaultMaxConcurrent, shared.MaxConcurrent);
    }
}
