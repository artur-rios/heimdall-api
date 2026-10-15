using ArturRios.Heimdall.Command.Services;
using ArturRios.Heimdall.Command.Tests.Support;
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
        // The captured instance, not Shared: a test may swap Shared briefly, and what this proves is
        // that the initializer installed a patient gate.
        var installed = CommandTestGate.Installed;

        // When
        var wait = installed.MaxWait;

        // Then
        Assert.True(wait >= TimeSpan.FromMinutes(1), $"the installed gate waits only {wait}");
        Assert.Equal(PasswordHashGate.DefaultMaxConcurrent, installed.MaxConcurrent);
    }
}
