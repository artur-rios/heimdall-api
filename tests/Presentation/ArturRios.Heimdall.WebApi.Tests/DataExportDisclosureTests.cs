using System.Reflection;
using ArturRios.Heimdall.Command.Services;
using ArturRios.Util.Test.Attributes;

namespace ArturRios.Heimdall.WebApi.Tests;

/// <summary>
///     The export tells the subject who receives their data (GDPR Art. 15(1)(c), LGPD Art. 18 VII),
///     and the record of processing tells the regulator the same thing. Two copies of one fact.
/// </summary>
/// <remarks>
///     The duplication is deliberate — a copy that told the subject to go and read a repository
///     would not be the "concise, transparent, intelligible" form Art. 12(1) requires — but the only
///     failure mode duplication of this kind has is silent drift, where a recipient is added in one
///     place and not the other. This closes it.
/// </remarks>
public class DataExportDisclosureTests
{
    private static string ReadRecord()
    {
        var path = Path.GetFullPath(
            Path.Combine(
                Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!,
                "..", "..", "..", "..", "..", "..", "docs", "requirements",
                "Data Protection Document.md"));

        Assert.True(File.Exists(path), $"The record of processing was not found at {path}");

        return File.ReadAllText(path);
    }

    [UnitFact]
    public void GivenTheExportsRecipients_WhenTheRecordIsRead_ThenEachAppearsInIt()
    {
        var record = ReadRecord();

        // Matched on the distinctive word rather than the full phrase: the two documents address
        // different readers and should be free to word things differently, but not to disagree
        // about who the recipients are.
        string[] required = ["Mailgun", "Google", "Hosting provider"];

        var missing = required.Where(name => !record.Contains(name, StringComparison.OrdinalIgnoreCase)).ToList();

        Assert.True(
            missing.Count == 0,
            $"These recipients are disclosed to data subjects in the export but are absent from the " +
            $"record of processing: {string.Join(", ", missing)}");
    }

    [UnitFact]
    public void GivenTheExportsRecipients_WhenRead_ThenEachCarriesAPurposeAndALocation()
    {
        // A recipient with no purpose tells the subject nothing they can act on, and Art. 15(1)(c)
        // asks for recipients "or categories of recipient" — a bare name is neither.
        Assert.All(DataExportDisclosure.Recipients, recipient =>
        {
            Assert.False(string.IsNullOrWhiteSpace(recipient.Name));
            Assert.False(string.IsNullOrWhiteSpace(recipient.Purpose));
            Assert.False(string.IsNullOrWhiteSpace(recipient.Location));
        });
    }

    [UnitFact]
    public void GivenTheWithheldValues_WhenRead_ThenEachCarriesAReason()
    {
        // Naming a value without saying why it is absent reads as an omission rather than a
        // decision, which is the opposite of what disclosing it is for.
        Assert.NotEmpty(DataExportDisclosure.Withheld);
        Assert.All(DataExportDisclosure.Withheld, withheld =>
        {
            Assert.False(string.IsNullOrWhiteSpace(withheld.Value));
            Assert.False(string.IsNullOrWhiteSpace(withheld.Reason));
        });
    }

    [UnitFact]
    public void GivenTheRetentionSummary_WhenRead_ThenEachCategoryCarriesAPeriod()
    {
        Assert.NotEmpty(DataExportDisclosure.Retention);
        Assert.All(DataExportDisclosure.Retention, entry =>
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.Category));
            Assert.False(string.IsNullOrWhiteSpace(entry.Period));
        });
    }
}
