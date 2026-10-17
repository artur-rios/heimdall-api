using ArturRios.Heimdall.Command.Services;
using ArturRios.Util.Test.Attributes;

namespace ArturRios.Heimdall.Command.Tests;

// Unit tests for ScopeInvitationOptions (FR-SC-18): HEIMDALL_SCOPE_INVITATION_LIFETIME_IN_SECONDS, seven days
// by default, accepted from 3600 to 2592000, and anything else falling back to the default rather than to
// an invitation that never expires or that nobody could answer in time.
public class ScopeInvitationOptionsTests
{
    [UnitFact]
    public void GivenNoValue_WhenRead_ThenTheLifetimeIsSevenDays()
    {
        // Given / When
        var options = ScopeInvitationOptions.From(null);

        // Then
        Assert.Equal(TimeSpan.FromDays(7), options.Lifetime);
        Assert.Null(options.RejectedLifetime);
    }

    [UnitTheory]
    [InlineData("3600", 3600)]
    [InlineData("86400", 86400)]
    [InlineData("2592000", 2592000)]
    public void GivenSecondsInRange_WhenRead_ThenTheyAreUsed(string configured, double seconds)
    {
        // Given / When
        var options = ScopeInvitationOptions.From(configured);

        // Then
        Assert.Equal(TimeSpan.FromSeconds(seconds), options.Lifetime);
        Assert.Null(options.RejectedLifetime);
    }

    [UnitTheory]
    [InlineData("3599")]
    [InlineData("2592001")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("7d")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    public void GivenAnUnusableValue_WhenRead_ThenTheDefaultAppliesAndTheValueIsReported(string configured)
    {
        // Given / When
        var options = ScopeInvitationOptions.From(configured);

        // Then
        Assert.Equal(TimeSpan.FromSeconds(ScopeInvitationOptions.DefaultLifetimeSeconds), options.Lifetime);
        Assert.Equal(configured, options.RejectedLifetime);
    }

    [UnitFact]
    public void GivenTheVariable_WhenNamed_ThenItIsTheDocumentedOne()
    {
        // Given
        // the variable the operations documentation names

        // When
        var variable = ScopeInvitationOptions.LifetimeVariable;

        // Then
        Assert.Equal("HEIMDALL_SCOPE_INVITATION_LIFETIME_IN_SECONDS", variable);
    }
}
