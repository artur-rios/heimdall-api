using System.Reflection;
using System.Text.RegularExpressions;
using ArturRios.Heimdall.WebApi.Monitoring;
using ArturRios.Util.Test.Attributes;

namespace ArturRios.Heimdall.WebApi.Tests;

/// <summary>
///     NFR-26's signals are only detected if something collects them, and what collects them is
///     scripts/security_signals.py, which matches on the marker. The marker is therefore a contract
///     between a C# constant and a Python string with no compiler in between.
/// </summary>
/// <remarks>
///     The failure mode is the one this whole feature exists to close. Rename the constant and
///     everything still builds, every test still passes, the service still writes its warnings —
///     and the collector matches nothing, for ever, reporting a quiet week every week. Because both
///     breach notification clocks run from awareness, a collector that has silently stopped is
///     indistinguishable from having no detection at all, and worse, because it looks like it works.
/// </remarks>
public partial class SecuritySignalMarkerTests
{
    [GeneratedRegex(@"^MARKER\s*=\s*""([^""]+)""", RegexOptions.Multiline)]
    private static partial Regex CollectorMarker();

    private static string CollectorPath() => Path.GetFullPath(
        Path.Combine(
            Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!,
            "..", "..", "..", "..", "..", "..", "scripts", "security_signals.py"));

    [UnitFact]
    public void GivenTheCollector_WhenItsMarkerIsRead_ThenItMatchesTheOneTheServiceWrites()
    {
        var path = CollectorPath();

        Assert.True(File.Exists(path), $"The signal collector was not found at {path}");

        var match = CollectorMarker().Match(File.ReadAllText(path));

        Assert.True(match.Success, "The collector has no MARKER = \"...\" to compare against");

        Assert.Equal(SecurityMonitoringService.Marker, match.Groups[1].Value);
    }

    [UnitFact]
    public void GivenTheMarker_WhenItIsInspected_ThenItIsDistinctiveEnoughToGrepFor()
    {
        // A marker that occurs in ordinary log prose would make every collection a false alert.
        Assert.Matches("^[A-Z][A-Z_]{7,}$", SecurityMonitoringService.Marker);
    }
}
