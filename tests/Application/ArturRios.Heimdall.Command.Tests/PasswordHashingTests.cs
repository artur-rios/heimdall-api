using ArturRios.Heimdall.Command.Services;
using ArturRios.Util.Hashing;
using ArturRios.Util.Test.Attributes;

namespace ArturRios.Heimdall.Command.Tests;

public class PasswordHashingTests
{
    [UnitFact]
    public void GivenTheVariableUnset_WhenReadingTheCost_ThenItIsTheProductionCost()
    {
        // Given
        static string? Unset(string _) => null;

        // When
        var configuration = PasswordHashing.FromEnvironment(Unset);

        // Then
        Assert.Equal(HashConfiguration.DefaultDegreeOfParallelism, configuration.DegreeOfParallelism);
        Assert.Equal(HashConfiguration.DefaultNumberOfIterations, configuration.NumberOfIterations);
        Assert.Equal(HashConfiguration.DefaultMemoryToUseInKb, configuration.MemoryToUseInKb);
    }

    [UnitTheory]
    [InlineData("true")]
    [InlineData("TRUE")]
    [InlineData(" True ")]
    public void GivenTheVariableTrue_WhenReadingTheCost_ThenItIsTheInsecureCost(string value)
    {
        // Given
        string? Read(string name) => name == PasswordHashing.InsecureTestVariable ? value : null;

        // When
        var configuration = PasswordHashing.FromEnvironment(Read);

        // Then
        Assert.Same(PasswordHashing.Insecure, configuration);
    }

    [UnitTheory]
    [InlineData("")]
    [InlineData("false")]
    [InlineData("1")]
    [InlineData("yes")]
    public void GivenTheVariableAnythingButTrue_WhenReadingTheCost_ThenItIsTheProductionCost(string value)
    {
        // Given
        string? Read(string name) => name == PasswordHashing.InsecureTestVariable ? value : null;

        // When
        var configuration = PasswordHashing.FromEnvironment(Read);

        // Then
        Assert.NotSame(PasswordHashing.Insecure, configuration);
        Assert.Equal(HashConfiguration.DefaultMemoryToUseInKb, configuration.MemoryToUseInKb);
    }

    [UnitFact]
    public void GivenTheInsecureCost_WhenStartingDeployed_ThenStartUpIsRefusedNamingTheVariable()
    {
        // Given
        var configuration = PasswordHashing.Insecure;

        // When
        var exception = Assert.Throws<InvalidOperationException>(
            () => PasswordHashing.EnsureAllowed(true, configuration));

        // Then
        Assert.Contains(PasswordHashing.InsecureTestVariable, exception.Message);
    }

    [UnitFact]
    public void GivenTheInsecureCost_WhenStartingUndeployed_ThenStartUpProceeds()
    {
        // Given
        var configuration = PasswordHashing.Insecure;

        // When
        var exception = Record.Exception(() => PasswordHashing.EnsureAllowed(false, configuration));

        // Then
        Assert.Null(exception);
    }

    [UnitFact]
    public void GivenTheProductionCost_WhenStartingDeployed_ThenStartUpProceeds()
    {
        // Given
        var configuration = new HashConfiguration();

        // When
        var exception = Record.Exception(() => PasswordHashing.EnsureAllowed(true, configuration));

        // Then
        Assert.Null(exception);
    }

    [UnitFact]
    public void GivenTheTestRun_WhenReadingTheCostInEffect_ThenItIsTheInsecureCost()
    {
        // Given
        // tests/default.runsettings sets the variable for every test project.

        // When
        var configuration = PasswordHashing.Configuration;

        // Then
        Assert.Same(PasswordHashing.Insecure, configuration);
    }

    [UnitFact]
    public void GivenTheInsecureCost_WhenDerivingAndVerifying_ThenTheyAgree()
    {
        // Given
        var hash = Hash.EncodeWithRandomSalt("a password", out var salt, PasswordHashing.Insecure);

        // When
        var matches = Hash.TextMatches("a password", hash, salt, PasswordHashing.Insecure);
        var wrong = Hash.TextMatches("another password", hash, salt, PasswordHashing.Insecure);

        // Then
        Assert.True(matches);
        Assert.False(wrong);
    }
}
