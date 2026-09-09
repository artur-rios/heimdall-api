using ArturRios.Heimdall.Shared.Security;
using ArturRios.Util.Test.Attributes;

namespace ArturRios.Heimdall.Shared.Tests;

// Unit tests for LogSafeEmail (NFR-22). Two properties have to hold at once, and they pull against
// each other: the reference must not disclose the address, and it must still correlate — whether
// three failures in a log are one address retrying or three people is the first question asked of a
// delivery problem.
public class LogSafeEmailTests
{
    private const string Address = "ada@analytical.engine";

    [UnitFact]
    public void GivenAnAddress_WhenReferenced_ThenTheAddressDoesNotAppear()
    {
        var reference = LogSafeEmail.Reference(Address);

        Assert.DoesNotContain("ada", reference);
        Assert.DoesNotContain("analytical", reference);
        Assert.DoesNotContain("@", reference);
    }

    [UnitFact]
    public void GivenTheSameAddressTwice_WhenReferenced_ThenTheReferenceIsStable()
    {
        // Without this the reference correlates nothing and may as well be omitted
        Assert.Equal(LogSafeEmail.Reference(Address), LogSafeEmail.Reference(Address));
    }

    [UnitFact]
    public void GivenDifferentAddresses_WhenReferenced_ThenTheReferencesDiffer()
    {
        Assert.NotEqual(LogSafeEmail.Reference(Address), LogSafeEmail.Reference("grace@navy.mil"));
    }

    [UnitTheory]
    [InlineData("ADA@ANALYTICAL.ENGINE")]
    [InlineData("  ada@analytical.engine  ")]
    [InlineData("Ada@Analytical.Engine")]
    public void GivenTheSameAddressWrittenDifferently_WhenReferenced_ThenItStillCorrelates(string variant)
    {
        // Two call sites disagreeing about casing must not produce two references for one person
        Assert.Equal(LogSafeEmail.Reference(Address), LogSafeEmail.Reference(variant));
    }

    [UnitTheory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void GivenNoAddress_WhenReferenced_ThenASafePlaceholderIsReturned(string? email)
    {
        // Never an empty string, which would read as a missing field rather than as no address
        Assert.Equal("(none)", LogSafeEmail.Reference(email));
    }
}
