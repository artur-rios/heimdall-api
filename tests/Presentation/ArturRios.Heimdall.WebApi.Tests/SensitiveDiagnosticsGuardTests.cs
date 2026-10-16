using ArturRios.Heimdall.WebApi.Security;
using ArturRios.Util.Test.Attributes;

namespace ArturRios.Heimdall.WebApi.Tests;

// Unit tests for SensitiveDiagnosticsGuard (NFR-28): an instance that serves a public host refuses to
// start with the diagnostics that expose data unless HEIMDALL_ALLOW_SENSITIVE_DIAGNOSTICS is true.
// Through Evaluate rather than the environment, which the functional suite's hosts read.
public class SensitiveDiagnosticsGuardTests
{
    private const string PublicHost = "heimdall-api-dev.example.com";
    private const string UiHost = "heimdall-dev.example.com";

    [UnitFact]
    public void GivenNoDiagnostics_WhenEvaluatingWithPublicHosts_ThenTheyAreOff()
    {
        // When
        var decision = SensitiveDiagnosticsGuard.Evaluate("Production", false, false, PublicHost, UiHost, null);

        // Then
        Assert.Equal(SensitiveDiagnosticsDecision.Off, decision);
    }

    [UnitTheory]
    [InlineData(null, null)]
    [InlineData("", "  ")]
    [InlineData("localhost", "localhost:8081")]
    [InlineData("127.0.0.1", "[::1]:8080")]
    [InlineData("api.localhost", "http://localhost:8081/")]
    public void GivenDiagnosticsAndOnlyLocalHosts_WhenEvaluating_ThenTheInstanceIsPrivate(string? publicHost, string? uiHost)
    {
        // When
        var decision = SensitiveDiagnosticsGuard.Evaluate("Development", true, true, publicHost, uiHost, null);

        // Then
        Assert.Equal(SensitiveDiagnosticsDecision.Private, decision);
    }

    [UnitFact]
    public void GivenSensitiveDataLoggingOnAPublicHost_WhenEvaluatingWithoutTheOverride_ThenStartUpIsRefused()
    {
        // When — Staging: EF diagnostics, no developer exception page
        var error = Assert.Throws<InvalidOperationException>(
            () => SensitiveDiagnosticsGuard.Evaluate("Staging", true, false, PublicHost, null, null));

        // Then — it names the setting, the host and the way out
        Assert.Contains("Refusing to start", error.Message);
        Assert.Contains("Staging", error.Message);
        Assert.Contains("sensitive data logging", error.Message);
        Assert.DoesNotContain("developer exception page", error.Message);
        Assert.Contains($"{SensitiveDiagnosticsGuard.PublicHostVariable}={PublicHost}", error.Message);
        Assert.Contains($"{SensitiveDiagnosticsGuard.AllowVariable}=true", error.Message);
    }

    [UnitFact]
    public void GivenTheDeveloperExceptionPageOnThePublicUiHost_WhenEvaluatingWithoutTheOverride_ThenStartUpIsRefused()
    {
        // When — the UI's host alone is enough
        var error = Assert.Throws<InvalidOperationException>(
            () => SensitiveDiagnosticsGuard.Evaluate("Development", false, true, "localhost", UiHost, ""));

        // Then
        Assert.Contains("developer exception page", error.Message);
        Assert.Contains($"{SensitiveDiagnosticsGuard.UiHostVariable}={UiHost}", error.Message);
        Assert.DoesNotContain(SensitiveDiagnosticsGuard.PublicHostVariable + "=", error.Message);
    }

    [UnitTheory]
    [InlineData("false")]
    [InlineData("1")]
    [InlineData("yes")]
    public void GivenAnOverrideOtherThanTrue_WhenEvaluatingOnAPublicHost_ThenStartUpIsRefused(string allow)
    {
        Assert.Throws<InvalidOperationException>(
            () => SensitiveDiagnosticsGuard.Evaluate("Development", true, true, PublicHost, UiHost, allow));
    }

    [UnitTheory]
    [InlineData("true")]
    [InlineData("TRUE")]
    [InlineData(" True ")]
    public void GivenTheOverride_WhenEvaluatingOnAPublicHost_ThenTheInstanceIsAllowed(string allow)
    {
        // When
        var decision = SensitiveDiagnosticsGuard.Evaluate("Development", true, true, PublicHost, UiHost, allow);

        // Then
        Assert.Equal(SensitiveDiagnosticsDecision.Allowed, decision);
    }

    [UnitTheory]
    [InlineData("heimdall.example.com", true)]
    [InlineData("https://heimdall.example.com/api/", true)]
    [InlineData("heimdall.example.com:443", true)]
    [InlineData("203.0.113.10", true)]
    [InlineData("2001:db8::1", true)]
    [InlineData("0.0.0.0", true)]
    [InlineData("localhost", false)]
    [InlineData("LOCALHOST.", false)]
    [InlineData("127.0.0.2", false)]
    [InlineData("::1", false)]
    [InlineData("", false)]
    public void GivenAHostValue_WhenCheckingWhetherItIsPublic_ThenOnlyLoopbackAndEmptyAreNot(string value, bool expected)
    {
        Assert.Equal(expected, SensitiveDiagnosticsGuard.IsPublicHost(value));
    }
}
