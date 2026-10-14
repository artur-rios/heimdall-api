using ArturRios.Heimdall.WebApi.Security;
using ArturRios.Util.Test.Attributes;

namespace ArturRios.Heimdall.WebApi.Tests;

// Unit tests for TokenLifetimeSetting, the parse behind HEIMDALL_AUTH_TOKEN_EXPIRATION_IN_SECONDS:
// an unusable value falls back to the default and says so, an absent one is just the default.
public class TokenLifetimeSettingTests
{
    [UnitTheory]
    [InlineData("soon")]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("NaN")]
    public void GivenAnUnusableValue_WhenParsed_ThenTheDefaultAppliesAndTheValueIsReported(string raw)
    {
        // Given / When
        var setting = TokenLifetimeSetting.Parse(raw, 3600);

        // Then
        Assert.Equal(3600, setting.Seconds);
        Assert.Equal(raw, setting.RejectedValue);
    }

    [UnitTheory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void GivenNoValue_WhenParsed_ThenTheDefaultAppliesWithoutAFallback(string? raw)
    {
        // Given / When
        var setting = TokenLifetimeSetting.Parse(raw, 3600);

        // Then
        Assert.Equal(3600, setting.Seconds);
        Assert.Null(setting.RejectedValue);
    }

    [UnitFact]
    public void GivenAUsableValue_WhenParsed_ThenItIsUsedWithoutAFallback()
    {
        // Given / When
        var setting = TokenLifetimeSetting.Parse("900", 3600);

        // Then
        Assert.Equal(900, setting.Seconds);
        Assert.Null(setting.RejectedValue);
    }
}
