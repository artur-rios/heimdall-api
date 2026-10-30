using System.Text.RegularExpressions;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.WebApi.Retention;
using ArturRios.Util.Test.Attributes;

namespace ArturRios.Heimdall.WebApi.Tests;

/// <summary>
///     The purge's log line lists the rows it removed by kind, and the kinds have to add up to the
///     total it opens with (#225).
/// </summary>
public class TokenRetentionSummaryTests
{
    private static int ListedTotal(string summary) =>
        Regex.Matches(summary[(summary.IndexOf(':') + 1)..], @"(\d+) [a-z]")
            .Sum(match => int.Parse(match.Groups[1].Value));

    [UnitFact]
    public void GivenARunThatPurgedOnlyApplicationCredentials_WhenSummarised_ThenTheListShowsThem()
    {
        var summary = TokenRetentionService.Summarise(new PurgeExpiredTokensCommandOutput
        {
            ApplicationCredentialsPurged = 3
        });

        Assert.NotNull(summary);
        Assert.Contains("removed 3 rows", summary);
        Assert.Contains("3 application credential", summary);
        Assert.Equal(3, ListedTotal(summary));
    }

    [UnitFact]
    public void GivenEveryKindPurged_WhenSummarised_ThenTheListedCountsAddUpToTheTotal()
    {
        // Distinct powers of two, so a missing or doubled term cannot cancel out
        var purged = new PurgeExpiredTokensCommandOutput
        {
            PasswordResetTokensPurged = 1,
            EmailVerificationTokensPurged = 2,
            TwoFactorEmailCodesPurged = 4,
            PendingRegistrationsPurged = 8,
            ApplicationCredentialsPurged = 16,
            AttemptNoticeBudgetsPurged = 32,
            GoogleIdTokenUsesPurged = 64,
            ScopeOwnerInvitationsPurged = 128
        };

        var summary = TokenRetentionService.Summarise(purged);

        Assert.NotNull(summary);
        Assert.Equal(purged.TotalPurged, ListedTotal(summary));
    }

    [UnitFact]
    public void GivenNothingPurged_WhenSummarised_ThenThereIsNothingToLog()
    {
        Assert.Null(TokenRetentionService.Summarise(new PurgeExpiredTokensCommandOutput()));
    }
}
