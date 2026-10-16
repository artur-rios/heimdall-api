using ArturRios.Heimdall.WebApi.Security;
using ArturRios.Util.Test.Attributes;

namespace ArturRios.Heimdall.WebApi.Tests;

// Unit tests for SensitiveDiagnosticsGuard (NFR-28): on an instance that serves a public host, EF
// Core's sensitive data logging and detailed errors and the developer exception page are turned off
// together, unless HEIMDALL_ALLOW_SENSITIVE_DIAGNOSTICS is true. Nothing here refuses to start.
// Through Evaluate rather than the environment, which the functional suite's hosts read.
public class SensitiveDiagnosticsGuardTests
{
    private const string PublicHost = "heimdall-api-dev.example.com";
    private const string UiHost = "heimdall-dev.example.com";

    private static PublicHostSettings Hosts(
        string? publicHost = null, string? uiHost = null, string? verificationUrl = null,
        string? resetUrl = null, string? corsOrigins = null) =>
        new(publicHost, uiHost, verificationUrl, resetUrl, corsOrigins);

    [UnitFact]
    public void GivenNoDiagnostics_WhenEvaluatingWithPublicHosts_ThenTheyAreOff()
    {
        // When — Production asks for none
        var diagnostics = SensitiveDiagnosticsGuard.Evaluate(false, false, Hosts(PublicHost, UiHost), null);

        // Then
        Assert.Equal(SensitiveDiagnosticsDecision.Off, diagnostics.Decision);
        Assert.False(diagnostics.DataLogging);
        Assert.False(diagnostics.DeveloperExceptionPage);
    }

    [UnitTheory]
    [InlineData(null, null)]
    [InlineData("", "  ")]
    [InlineData("localhost", "localhost:8081")]
    [InlineData("127.0.0.1", "[::1]:8080")]
    [InlineData("api.localhost", "http://localhost:8081/")]
    public void GivenDiagnosticsAndOnlyLocalHosts_WhenEvaluating_ThenTheyStayOn(string? publicHost, string? uiHost)
    {
        // When
        var diagnostics = SensitiveDiagnosticsGuard.Evaluate(true, true, Hosts(publicHost, uiHost), null);

        // Then
        Assert.Equal(SensitiveDiagnosticsDecision.Private, diagnostics.Decision);
        Assert.True(diagnostics.DataLogging);
        Assert.True(diagnostics.DeveloperExceptionPage);
    }

    [UnitFact]
    public void GivenTheLocalTemplatesUrlsAndOrigins_WhenEvaluating_ThenTheInstanceIsPrivate()
    {
        // Given what docker/local.env.example sets
        var hosts = Hosts(
            verificationUrl: "http://localhost:8081/verify-email",
            resetUrl: "http://localhost:8081/password-reset",
            corsOrigins: "http://localhost:8081, http://127.0.0.1:8082");

        // When
        var diagnostics = SensitiveDiagnosticsGuard.Evaluate(true, true, hosts, null);

        // Then
        Assert.Equal(SensitiveDiagnosticsDecision.Private, diagnostics.Decision);
    }

    [UnitFact]
    public void GivenTemplatePlaceholders_WhenEvaluating_ThenTheyAreNotTakenForHosts()
    {
        // Given the tracked Environments/.env template, whose values are placeholders
        var hosts = Hosts(
            verificationUrl: "[[email-verification-url]]",
            resetUrl: "[[password-reset-url]]",
            corsOrigins: "[[cors-allowed-origins]]");

        // When
        var diagnostics = SensitiveDiagnosticsGuard.Evaluate(true, true, hosts, null);

        // Then
        Assert.Equal(SensitiveDiagnosticsDecision.Private, diagnostics.Decision);
    }

    [UnitFact]
    public void GivenDevelopmentOnAPublicHost_WhenEvaluatingWithoutTheOverride_ThenEveryDiagnosticIsOff()
    {
        // When — Development asks for EF diagnostics and the developer exception page
        var diagnostics = SensitiveDiagnosticsGuard.Evaluate(true, true, Hosts(PublicHost, UiHost), null);

        // Then — both off, by the one decision
        Assert.Equal(SensitiveDiagnosticsDecision.Suppressed, diagnostics.Decision);
        Assert.False(diagnostics.DataLogging);
        Assert.False(diagnostics.DeveloperExceptionPage);
        Assert.Equal(
            new[] { $"{PublicHostSettings.PublicHostVariable}={PublicHost}", $"{PublicHostSettings.UiHostVariable}={UiHost}" },
            diagnostics.PublicHosts);
    }

    [UnitFact]
    public void GivenStagingOnThePublicUiHostOnly_WhenEvaluatingWithoutTheOverride_ThenDataLoggingIsOff()
    {
        // When — Staging: EF diagnostics, no developer exception page; the UI's host alone is enough
        var diagnostics = SensitiveDiagnosticsGuard.Evaluate(true, false, Hosts("localhost", UiHost), "");

        // Then
        Assert.Equal(SensitiveDiagnosticsDecision.Suppressed, diagnostics.Decision);
        Assert.False(diagnostics.DataLogging);
        Assert.False(diagnostics.DeveloperExceptionPage);
        Assert.Equal(new[] { $"{PublicHostSettings.UiHostVariable}={UiHost}" }, diagnostics.PublicHosts);
    }

    [UnitTheory]
    [InlineData("https://heimdall-dev.example.com/verify-email", null, null, PublicHostSettings.VerificationUrlVariable)]
    [InlineData(null, "https://heimdall-dev.example.com/password-reset", null, PublicHostSettings.PasswordResetUrlVariable)]
    [InlineData(null, null, "http://localhost:8081,https://heimdall-dev.example.com", PublicHostSettings.CorsAllowedOriginsVariable)]
    public void GivenOnlyAUrlOrAnOriginNamesAPublicHost_WhenEvaluating_ThenDiagnosticsAreOff(
        string? verificationUrl, string? resetUrl, string? corsOrigins, string variable)
    {
        // Given no host variable at all — an instance started without the two Traefik names
        var hosts = Hosts(verificationUrl: verificationUrl, resetUrl: resetUrl, corsOrigins: corsOrigins);

        // When
        var diagnostics = SensitiveDiagnosticsGuard.Evaluate(true, true, hosts, null);

        // Then — the host is named, and nothing else of the value
        Assert.Equal(SensitiveDiagnosticsDecision.Suppressed, diagnostics.Decision);
        Assert.Equal(new[] { $"{variable}={UiHost}" }, diagnostics.PublicHosts);
    }

    [UnitTheory]
    [InlineData("heimdall-dev.example.com/verify-email", null, null, PublicHostSettings.VerificationUrlVariable)]
    [InlineData(null, "heimdall-dev.example.com:8443/password-reset", null, PublicHostSettings.PasswordResetUrlVariable)]
    [InlineData(null, null, "localhost:8081, heimdall-dev.example.com", PublicHostSettings.CorsAllowedOriginsVariable)]
    public void GivenAUrlOrAnOriginWithoutAScheme_WhenEvaluating_ThenItsHostCounts(
        string? verificationUrl, string? resetUrl, string? corsOrigins, string variable)
    {
        // Given a value written without http:// or https://
        var hosts = Hosts(verificationUrl: verificationUrl, resetUrl: resetUrl, corsOrigins: corsOrigins);

        // When
        var diagnostics = SensitiveDiagnosticsGuard.Evaluate(true, true, hosts, null);

        // Then — read as a host name, and only the host is named
        Assert.Equal(SensitiveDiagnosticsDecision.Suppressed, diagnostics.Decision);
        Assert.Equal(new[] { $"{variable}={UiHost}" }, diagnostics.PublicHosts);
    }

    [UnitFact]
    public void GivenLocalUrlsAndOriginsWithoutAScheme_WhenEvaluating_ThenTheInstanceIsPrivate()
    {
        // Given
        var hosts = Hosts(
            verificationUrl: "localhost:8081/verify-email",
            resetUrl: "127.0.0.1:8081/password-reset",
            corsOrigins: "localhost:8081, [::1]:8082");

        // When
        var diagnostics = SensitiveDiagnosticsGuard.Evaluate(true, true, hosts, null);

        // Then
        Assert.Equal(SensitiveDiagnosticsDecision.Private, diagnostics.Decision);
    }

    [UnitTheory]
    [InlineData("false")]
    [InlineData("1")]
    [InlineData("yes")]
    public void GivenAnOverrideOtherThanTrue_WhenEvaluatingOnAPublicHost_ThenEveryDiagnosticIsOff(string allow)
    {
        // When
        var diagnostics = SensitiveDiagnosticsGuard.Evaluate(true, true, Hosts(PublicHost, UiHost), allow);

        // Then
        Assert.Equal(SensitiveDiagnosticsDecision.Suppressed, diagnostics.Decision);
        Assert.False(diagnostics.DataLogging);
        Assert.False(diagnostics.DeveloperExceptionPage);
    }

    [UnitTheory]
    [InlineData("true")]
    [InlineData("TRUE")]
    [InlineData(" True ")]
    public void GivenTheOverride_WhenEvaluatingOnAPublicHost_ThenTheDiagnosticsStayOn(string allow)
    {
        // When
        var diagnostics = SensitiveDiagnosticsGuard.Evaluate(true, true, Hosts(PublicHost, UiHost), allow);

        // Then
        Assert.Equal(SensitiveDiagnosticsDecision.Allowed, diagnostics.Decision);
        Assert.True(diagnostics.DataLogging);
        Assert.True(diagnostics.DeveloperExceptionPage);
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
    [InlineData("[::1]", false)]
    [InlineData("", false)]
    public void GivenAHostValue_WhenCheckingWhetherItIsPublic_ThenOnlyLoopbackAndEmptyAreNot(string value, bool expected)
    {
        Assert.Equal(expected, SensitiveDiagnosticsGuard.IsPublicHost(value));
    }
}
