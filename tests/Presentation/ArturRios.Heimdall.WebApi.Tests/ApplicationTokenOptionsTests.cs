using ArturRios.Heimdall.WebApi.Security;
using ArturRios.Util.Test.Attributes;

namespace ArturRios.Heimdall.WebApi.Tests;

// Unit tests for ApplicationTokenOptions (UC-56, TH-35): the application audience is required and
// must differ from the person audience — the separation is what stops a consumer that pins the
// person audience from taking an application token for a person — and the lifetime defaults to ten
// minutes. Start-up refuses a missing or equal audience instead of running without the separation.
public class ApplicationTokenOptionsTests
{
    [UnitFact]
    public void GivenADistinctAudienceAndNoLifetime_WhenParsed_ThenTheLifetimeIsTenMinutes()
    {
        // Given / When
        var options = ApplicationTokenOptions.Parse("heimdall-applications", null, "heimdall");

        // Then
        Assert.Equal("heimdall-applications", options.Audience);
        Assert.Equal(TimeSpan.FromMinutes(10), options.Lifetime);
    }

    [UnitFact]
    public void GivenALifetimeInSeconds_WhenParsed_ThenItIsUsed()
    {
        // Given / When
        var options = ApplicationTokenOptions.Parse("heimdall-applications", "300", "heimdall");

        // Then
        Assert.Equal(TimeSpan.FromMinutes(5), options.Lifetime);
    }

    [UnitTheory]
    [InlineData("10m")]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("86401")]
    [InlineData("NaN")]
    public void GivenAnUnusableLifetime_WhenParsed_ThenTheDefaultApplies(string raw)
    {
        // Given / When
        var options = ApplicationTokenOptions.Parse("heimdall-applications", raw, "heimdall");

        // Then
        Assert.Equal(ApplicationTokenOptions.DefaultLifetime, options.Lifetime);
    }

    [UnitTheory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void GivenNoAudience_WhenParsed_ThenStartUpIsRefused(string? audience)
    {
        // Given / When
        var error = Assert.Throws<InvalidOperationException>(
            () => ApplicationTokenOptions.Parse(audience, null, "heimdall"));

        // Then
        Assert.Contains(ApplicationTokenOptions.AudienceVariable, error.Message);
    }

    [UnitTheory]
    [InlineData("heimdall", "heimdall")]
    [InlineData(" heimdall ", "heimdall")]
    [InlineData("heimdall/", "heimdall")]
    [InlineData("heimdall", "heimdall/")]
    public void GivenTheSameAudienceAsPersonTokens_WhenParsed_ThenStartUpIsRefused(string audience, string personAudience)
    {
        // Given — a trailing slash is the same audience: Microsoft.IdentityModel ignores it when
        // matching aud by default, so a consumer pinned to one would accept the other

        // When
        var error = Assert.Throws<InvalidOperationException>(
            () => ApplicationTokenOptions.Parse(audience, null, personAudience));

        // Then
        Assert.Contains(ApplicationTokenOptions.AudienceVariable, error.Message);
        Assert.Contains("HEIMDALL_AUTH_TOKEN_AUDIENCE", error.Message);
    }

    [UnitTheory]
    [InlineData("10m")]
    [InlineData("0")]
    [InlineData("86401")]
    public void GivenAnUnusableLifetime_WhenParsed_ThenTheFallbackIsReported(string raw)
    {
        // Given / When
        var options = ApplicationTokenOptions.Parse("heimdall-applications", raw, "heimdall");

        // Then
        Assert.Equal(raw, options.RejectedExpiration);
        Assert.Equal(ApplicationTokenOptions.DefaultLifetime, options.Lifetime);
    }

    [UnitTheory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void GivenNoLifetime_WhenParsed_ThenNoFallbackIsReported(string? raw)
    {
        // Given / When
        var options = ApplicationTokenOptions.Parse("heimdall-applications", raw, "heimdall");

        // Then — absent is the documented default, not a fallback
        Assert.Null(options.RejectedExpiration);
    }

    [UnitFact]
    public void GivenAUsableLifetime_WhenParsed_ThenNoFallbackIsReported()
    {
        // Given / When
        var options = ApplicationTokenOptions.Parse("heimdall-applications", "300", "heimdall");

        // Then
        Assert.Null(options.RejectedExpiration);
    }
}
