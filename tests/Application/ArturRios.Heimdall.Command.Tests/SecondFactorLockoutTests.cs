using ArturRios.Heimdall.Command.Services;
using ArturRios.Util.Test.Attributes;

namespace ArturRios.Heimdall.Command.Tests;

// Unit tests for SecondFactorLockout (FR-AU-12): how long each successive lock lasts.
public class SecondFactorLockoutTests
{
    [UnitFact]
    public void GivenAMoment_WhenTheLockoutEndsAreListed_ThenEachDoublesFromFifteenMinutesUpToADay()
    {
        // Given
        var now = new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);

        // When
        var ends = SecondFactorLockout.LockoutEndsFrom(now);

        // Then
        int[] minutes = [15, 30, 60, 120, 240, 480, 960, 1440];
        Assert.Equal(minutes.Select(minute => now.AddMinutes(minute)), ends);
    }
}
