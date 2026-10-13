using System.Text.Json;
using ArturRios.Util.Test.Attributes;

namespace ArturRios.Heimdall.WebApi.Tests;

// Guards the published document's licence against drift from the repository's LICENSE: the API
// reference must name the licence the project is actually distributed under (the Artur Rios
// Attribution License). The name is checked against the LICENSE file itself rather than a copy of
// it, so a change to either without the other fails here.
// [UnitFact]: no database, no host, just the committed files.
public class OpenApiInfoTests
{
    private const string LicenseUrl = "https://github.com/artur-rios/heimdall-api/blob/main/LICENSE";

    [UnitFact]
    public void GivenPublishedDocument_WhenLicenseRead_ThenItIsTheRepositoryLicense()
    {
        using var document = LoadDocument();
        var license = document.RootElement.GetProperty("info").GetProperty("license");

        Assert.Equal(LicenseHeading(), license.GetProperty("name").GetString());
        Assert.Equal(LicenseUrl, license.GetProperty("url").GetString());
    }

    [UnitFact]
    public void GivenRepositoryLicense_WhenRead_ThenItIsTheAttributionLicense()
    {
        // The test above would pass just as well if LICENSE and the document both moved to another
        // licence together. That would be a deliberate decision, and this is where it is made visible.
        Assert.Equal("Artur Rios Attribution License", LicenseHeading());
    }

    private static string LicenseHeading() =>
        File.ReadLines(Path.Combine(RepositoryRoot(), "LICENSE")).First().Trim();

    private static JsonDocument LoadDocument() =>
        JsonDocument.Parse(File.ReadAllText(
            Environment.GetEnvironmentVariable("HEIMDALL_OPENAPI_DOCUMENT")
            ?? Path.Combine(RepositoryRoot(), "docs", "openapi", "heimdall.json")));

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "src", "ArturRios.Heimdall.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
               ?? throw new InvalidOperationException(
                   $"Could not locate the repository root from {AppContext.BaseDirectory}");
    }
}
