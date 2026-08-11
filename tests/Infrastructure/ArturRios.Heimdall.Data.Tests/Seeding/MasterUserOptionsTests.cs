using ArturRios.Heimdall.Data.Seeding;
using ArturRios.Util.Test.Attributes;

namespace ArturRios.Heimdall.Data.Tests.Seeding;

public class MasterUserOptionsTests
{
    [UnitFact]
    public void GivenAllValuesPresent_WhenCompletenessChecked_ThenOptionsAreComplete()
    {
        var options = new MasterUserOptions("Master User", "master@heimdall.test", "Str0ng-Pass!");

        Assert.True(options.IsComplete);
    }

    [UnitTheory]
    [InlineData("", "master@heimdall.test", "Str0ng-Pass!")]
    [InlineData("Master User", "", "Str0ng-Pass!")]
    [InlineData("Master User", "master@heimdall.test", "")]
    [InlineData("   ", "master@heimdall.test", "Str0ng-Pass!")]
    public void GivenAMissingValue_WhenCompletenessChecked_ThenOptionsAreIncomplete(
        string name,
        string email,
        string password)
    {
        var options = new MasterUserOptions(name, email, password);

        Assert.False(options.IsComplete);
    }

    [UnitFact]
    public void GivenUnsetVariables_WhenReadFromEnvironment_ThenValuesAreEmptyAndIncomplete()
    {
        Environment.SetEnvironmentVariable(MasterUserOptions.NameVariable, null);
        Environment.SetEnvironmentVariable(MasterUserOptions.EmailVariable, null);
        Environment.SetEnvironmentVariable(MasterUserOptions.PasswordVariable, null);

        var options = MasterUserOptions.FromEnvironment();

        Assert.Equal(string.Empty, options.Name);
        Assert.Equal(string.Empty, options.Email);
        Assert.Equal(string.Empty, options.Password);
        Assert.False(options.IsComplete);
    }
}
