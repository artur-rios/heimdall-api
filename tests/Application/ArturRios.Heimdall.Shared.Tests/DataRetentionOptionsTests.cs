using ArturRios.Heimdall.Shared.Retention;
using ArturRios.Util.Test.Attributes;

namespace ArturRios.Heimdall.Shared.Tests;

// Unit tests for DataRetentionOptions (NFR-19). Two properties matter more than the parsing itself:
// that a deployment which sets nothing still enforces the published schedule — an unset variable
// meaning "keep forever" is the state NFR-19 exists to end — and that a value which would disable
// the grace period, or the purge's bound, is refused rather than honoured.
public class DataRetentionOptionsTests
{
    private static readonly string[] AllVariables =
    [
        DataRetentionOptions.SingleUseTokenGraceDaysVariable,
        DataRetentionOptions.PurgeIntervalMinutesVariable,
        DataRetentionOptions.PurgeBatchSizeVariable,
        DataRetentionOptions.PurgeEnabledVariable,
        DataRetentionOptions.SubjectErasureDeadlineDaysVariable,
        DataRetentionOptions.AdministrativeDeletionWindowDaysVariable,
        DataRetentionOptions.AnonymisationEnabledVariable
    ];

    private static void ClearAll()
    {
        foreach (var variable in AllVariables)
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [UnitFact]
    public void GivenUnsetVariables_WhenReadFromEnvironment_ThenTheDocumentedDefaultsApply()
    {
        ClearAll();

        var options = DataRetentionOptions.FromEnvironment();

        Assert.Equal(DataRetentionOptions.DefaultSingleUseTokenGrace, options.SingleUseTokenGrace);
        Assert.Equal(DataRetentionOptions.DefaultPurgeInterval, options.PurgeInterval);
        Assert.Equal(DataRetentionOptions.DefaultPurgeBatchSize, options.PurgeBatchSize);
        Assert.Equal(DataRetentionOptions.DefaultSubjectErasureDeadline, options.SubjectErasureDeadline);
        Assert.Equal(
            DataRetentionOptions.DefaultAdministrativeDeletionWindow, options.AdministrativeDeletionWindow);

        // Both passes are on unless an operator switches them off: silence must not mean "keep forever"
        Assert.True(options.PurgeEnabled);
        Assert.True(options.AnonymisationEnabled);

        // An absent variable is the default being chosen, not a fallback from a bad value
        Assert.Empty(options.InvalidVariables);
    }

    [UnitFact]
    public void GivenValidVariables_WhenReadFromEnvironment_ThenTheyAreApplied()
    {
        ClearAll();
        Environment.SetEnvironmentVariable(DataRetentionOptions.SingleUseTokenGraceDaysVariable, "3");
        Environment.SetEnvironmentVariable(DataRetentionOptions.PurgeIntervalMinutesVariable, "15");
        Environment.SetEnvironmentVariable(DataRetentionOptions.PurgeBatchSizeVariable, "50");
        Environment.SetEnvironmentVariable(DataRetentionOptions.PurgeEnabledVariable, "false");

        var options = DataRetentionOptions.FromEnvironment();

        Assert.Equal(TimeSpan.FromDays(3), options.SingleUseTokenGrace);
        Assert.Equal(TimeSpan.FromMinutes(15), options.PurgeInterval);
        Assert.Equal(50, options.PurgeBatchSize);
        Assert.False(options.PurgeEnabled);
        Assert.Empty(options.InvalidVariables);

        ClearAll();
    }

    [UnitTheory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("never")]
    [InlineData("")]
    public void GivenAnUnusableGracePeriod_WhenReadFromEnvironment_ThenTheDefaultApplies(string value)
    {
        // A zero or negative grace would purge a token the moment it expired, turning UC-13's
        // TokenExpired into TokenInvalid for anyone following a stale link
        ClearAll();
        Environment.SetEnvironmentVariable(DataRetentionOptions.SingleUseTokenGraceDaysVariable, value);

        var options = DataRetentionOptions.FromEnvironment();

        Assert.Equal(DataRetentionOptions.DefaultSingleUseTokenGrace, options.SingleUseTokenGrace);

        ClearAll();
    }

    [UnitTheory]
    [InlineData("100000")]
    [InlineData("1e18")]
    public void GivenAGracePeriodBeyondTheAcceptedRange_WhenReadFromEnvironment_ThenTheDefaultApplies(
        string value)
    {
        // A value this large is a stray unit, not a policy — and one large enough would overflow
        // TimeSpan.FromDays, which must not be allowed to throw during start-up
        ClearAll();
        Environment.SetEnvironmentVariable(DataRetentionOptions.SingleUseTokenGraceDaysVariable, value);

        var options = DataRetentionOptions.FromEnvironment();

        Assert.Equal(DataRetentionOptions.DefaultSingleUseTokenGrace, options.SingleUseTokenGrace);
        Assert.Contains(DataRetentionOptions.SingleUseTokenGraceDaysVariable, options.InvalidVariables);

        ClearAll();
    }

    [UnitTheory]
    [InlineData("0.5")]
    [InlineData("100000")]
    [InlineData("1e18")]
    public void GivenAPurgeIntervalOutsideTheAcceptedRange_WhenReadFromEnvironment_ThenTheDefaultApplies(
        string value)
    {
        // Below the floor the purge becomes continuous delete pressure; above the ceiling
        // PeriodicTimer refuses the period, and the throw would stop the host — so a stray digit in
        // a retention setting would take the API down
        ClearAll();
        Environment.SetEnvironmentVariable(DataRetentionOptions.PurgeIntervalMinutesVariable, value);

        var options = DataRetentionOptions.FromEnvironment();

        Assert.Equal(DataRetentionOptions.DefaultPurgeInterval, options.PurgeInterval);
        Assert.Contains(DataRetentionOptions.PurgeIntervalMinutesVariable, options.InvalidVariables);

        ClearAll();
    }

    [UnitTheory]
    [InlineData("0")]
    [InlineData("-10")]
    [InlineData("all")]
    public void GivenAnUnusableBatchSize_WhenReadFromEnvironment_ThenTheDefaultApplies(string value)
    {
        // An unbounded or empty batch would either do nothing or turn the purge into the sustained
        // delete pressure SRD §6.3.2 warns about
        ClearAll();
        Environment.SetEnvironmentVariable(DataRetentionOptions.PurgeBatchSizeVariable, value);

        var options = DataRetentionOptions.FromEnvironment();

        Assert.Equal(DataRetentionOptions.DefaultPurgeBatchSize, options.PurgeBatchSize);

        ClearAll();
    }

    [UnitTheory]
    [InlineData("31")]
    [InlineData("90")]
    [InlineData("3650")]
    public void GivenAnErasureDeadlineAboveTheStatutoryLimit_WhenReadFromEnvironment_ThenItIsRefused(
        string value)
    {
        // The one ceiling here that is not a sanity check. GDPR Art. 12(3) allows at most a month,
        // so a larger value is not a policy choice an operator gets to make — it is a compliance
        // failure, and configuration must not be able to express it.
        ClearAll();
        Environment.SetEnvironmentVariable(DataRetentionOptions.SubjectErasureDeadlineDaysVariable, value);

        var options = DataRetentionOptions.FromEnvironment();

        Assert.Equal(DataRetentionOptions.DefaultSubjectErasureDeadline, options.SubjectErasureDeadline);
        Assert.Contains(DataRetentionOptions.SubjectErasureDeadlineDaysVariable, options.InvalidVariables);

        ClearAll();
    }

    [UnitFact]
    public void GivenAShorterErasureDeadline_WhenReadFromEnvironment_ThenItIsApplied()
    {
        // Below the limit is always allowed: the statutory deadline is an outer bound, and acting
        // sooner is what the law actually asks for.
        ClearAll();
        Environment.SetEnvironmentVariable(DataRetentionOptions.SubjectErasureDeadlineDaysVariable, "7");

        var options = DataRetentionOptions.FromEnvironment();

        Assert.Equal(TimeSpan.FromDays(7), options.SubjectErasureDeadline);
        Assert.Empty(options.InvalidVariables);

        ClearAll();
    }

    [UnitTheory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1000")]
    [InlineData("whenever")]
    public void GivenAnUnusableDeletionWindow_WhenReadFromEnvironment_ThenTheDefaultApplies(string value)
    {
        ClearAll();
        Environment.SetEnvironmentVariable(DataRetentionOptions.AdministrativeDeletionWindowDaysVariable, value);

        var options = DataRetentionOptions.FromEnvironment();

        Assert.Equal(
            DataRetentionOptions.DefaultAdministrativeDeletionWindow, options.AdministrativeDeletionWindow);

        ClearAll();
    }

    [UnitFact]
    public void GivenUnusableValues_WhenReadFromEnvironment_ThenEachIsNamedForTheLog()
    {
        ClearAll();
        Environment.SetEnvironmentVariable(DataRetentionOptions.SingleUseTokenGraceDaysVariable, "soon");
        Environment.SetEnvironmentVariable(DataRetentionOptions.PurgeEnabledVariable, "maybe");

        var options = DataRetentionOptions.FromEnvironment();

        // Falling back silently would leave an operator believing a retention setting was applied
        Assert.Contains(DataRetentionOptions.SingleUseTokenGraceDaysVariable, options.InvalidVariables);
        Assert.Contains(DataRetentionOptions.PurgeEnabledVariable, options.InvalidVariables);
        Assert.True(options.PurgeEnabled);

        ClearAll();
    }
}
