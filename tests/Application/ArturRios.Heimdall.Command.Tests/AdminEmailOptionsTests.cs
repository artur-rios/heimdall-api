using ArturRios.Heimdall.Command.Services;
using ArturRios.Util.Test.Attributes;

namespace ArturRios.Heimdall.Command.Tests;

// Unit tests for AdminEmailOptions (FR-EV-07, FR-PR-06): the cooldown's reading of
// HEIMDALL_ADMIN_EMAIL_COOLDOWN_IN_SECONDS, which falls back to five minutes for anything unusable
// rather than switching the cooldown off, and the threshold that turns "issued within the window"
// into a comparison on a token's ExpiresAt.
public class AdminEmailOptionsTests
{
    [UnitFact]
    public void GivenNoValue_WhenRead_ThenTheCooldownIsFiveMinutes()
    {
        // Given / When
        var options = AdminEmailOptions.From(null);

        // Then
        Assert.Equal(TimeSpan.FromMinutes(5), options.Cooldown);
    }

    [UnitFact]
    public void GivenSeconds_WhenRead_ThenTheyAreUsed()
    {
        // Given / When
        var options = AdminEmailOptions.From("120");

        // Then
        Assert.Equal(TimeSpan.FromMinutes(2), options.Cooldown);
    }

    [UnitFact]
    public void GivenZero_WhenRead_ThenTheCooldownIsOff()
    {
        // Given / When
        var options = AdminEmailOptions.From("0");

        // Then
        Assert.Equal(TimeSpan.Zero, options.Cooldown);
    }

    [UnitFact]
    public void GivenTheUpperBound_WhenRead_ThenItIsAccepted()
    {
        // Given / When — a day is the longest cooldown accepted
        var options = AdminEmailOptions.From("86400");

        // Then
        Assert.Equal(TimeSpan.FromDays(1), options.Cooldown);
    }

    [UnitTheory]
    [InlineData("")]
    [InlineData("5m")]
    [InlineData("-1")]
    [InlineData("86401")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("1e300")]
    public void GivenAnUnusableValue_WhenRead_ThenTheDefaultApplies(string configured)
    {
        // Given a mistyped setting
        // When
        var options = AdminEmailOptions.From(configured);

        // Then — the default, not "no cooldown" and not a start-up crash
        Assert.Equal(TimeSpan.FromMinutes(5), options.Cooldown);
    }

    [UnitFact]
    public void GivenAWindow_WhenComputingTheThreshold_ThenItIsNowPlusLifetimeMinusCooldown()
    {
        // Given a token kind living one hour and a five-minute cooldown
        var options = new AdminEmailOptions { Cooldown = TimeSpan.FromMinutes(5) };
        var now = new DateTime(2026, 10, 14, 12, 0, 0, DateTimeKind.Utc);

        // When
        var threshold = options.CooldownThreshold(now, TimeSpan.FromHours(1));

        // Then — a token issued at 11:55 expires at 12:55; any later expiry was issued inside the window
        Assert.Equal(new DateTime(2026, 10, 14, 12, 55, 0, DateTimeKind.Utc), threshold);
    }
}
