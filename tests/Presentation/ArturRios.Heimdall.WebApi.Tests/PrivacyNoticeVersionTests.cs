using System.Reflection;
using System.Text.RegularExpressions;
using ArturRios.Heimdall.Command.Services;
using ArturRios.Util.Test.Attributes;

namespace ArturRios.Heimdall.WebApi.Tests;

/// <summary>
///     NFR-23 records, against every identity, the version of the privacy notice in force when it
///     was created. That record is only worth having if the version names a document somebody can
///     actually produce.
/// </summary>
/// <remarks>
///     The constant and the document are two copies of one fact, and the failure mode is silent
///     drift: the notice is edited, its version header is bumped, and the code keeps stamping the
///     old number — so every identity created afterwards claims to have been shown a version it was
///     not. That is worse than recording nothing, because it looks like evidence.
/// </remarks>
public partial class PrivacyNoticeVersionTests
{
    [GeneratedRegex(@"^\*\*Version\s+([0-9]+\.[0-9]+)", RegexOptions.Multiline)]
    private static partial Regex VersionHeader();

    [UnitFact]
    public void GivenThePrivacyNotice_WhenItsVersionIsRead_ThenTheRecordedVersionMatches()
    {
        var path = Path.GetFullPath(
            Path.Combine(
                Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!,
                "..", "..", "..", "..", "..", "..", "docs", "requirements", "Privacy Notice.md"));

        Assert.True(File.Exists(path), $"The privacy notice was not found at {path}");

        var match = VersionHeader().Match(File.ReadAllText(path));

        Assert.True(match.Success, "The privacy notice has no '**Version x.y' header to compare against");

        Assert.Equal(match.Groups[1].Value, LegalBasisRecorder.CurrentPrivacyNoticeVersion);
    }
}
