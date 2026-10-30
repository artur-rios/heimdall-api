using ArturRios.Heimdall.Command.Services;
using ArturRios.Util.Test.Attributes;

namespace ArturRios.Heimdall.Command.Tests;

// Unit tests for RegistrationOptions (#206): HEIMDALL_REGISTRATION_MAX_PENDING_PER_SCOPE, 1000 by default, any
// positive integer accepted, and anything else falling back to the default rather than to no cap at all.
public class RegistrationOptionsTests
{
    [UnitFact]
    public void GivenNoValue_WhenRead_ThenTheCapIsAThousand()
    {
        // Given / When
        var options = RegistrationOptions.From(null);

        // Then
        Assert.Equal(1000, options.MaxPendingPerScope);
        Assert.Null(options.RejectedMaxPendingPerScope);
    }

    [UnitTheory]
    [InlineData("1", 1)]
    [InlineData("250", 250)]
    [InlineData("100000", 100000)]
    public void GivenAPositiveInteger_WhenRead_ThenItIsUsed(string configured, int cap)
    {
        // Given / When
        var options = RegistrationOptions.From(configured);

        // Then
        Assert.Equal(cap, options.MaxPendingPerScope);
        Assert.Null(options.RejectedMaxPendingPerScope);
    }

    [UnitTheory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("12.5")]
    [InlineData("lots")]
    [InlineData("99999999999")]
    public void GivenAnUnusableValue_WhenRead_ThenTheDefaultAppliesAndTheValueIsReported(string configured)
    {
        // Given / When
        var options = RegistrationOptions.From(configured);

        // Then
        Assert.Equal(RegistrationOptions.DefaultMaxPendingPerScope, options.MaxPendingPerScope);
        Assert.Equal(configured, options.RejectedMaxPendingPerScope);
    }

    [UnitFact]
    public void GivenTheVariable_WhenNamed_ThenItIsTheDocumentedOne()
    {
        // Given / When
        var variable = RegistrationOptions.MaxPendingPerScopeVariable;

        // Then
        Assert.Equal("HEIMDALL_REGISTRATION_MAX_PENDING_PER_SCOPE", variable);
    }
}
