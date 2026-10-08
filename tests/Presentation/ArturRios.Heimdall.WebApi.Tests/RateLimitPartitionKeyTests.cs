using System.Net;
using ArturRios.Util.Test.Attributes;

namespace ArturRios.Heimdall.WebApi.Tests;

// Unit tests for the partition the credential-endpoint rate limiter counts a caller in. An IPv6
// subscriber holds a whole /64, so counting per address would let one caller spread requests over
// 2^64 partitions and never meet the limit.
public class RateLimitPartitionKeyTests
{
    [UnitFact]
    public void GivenTwoAddressesInOneIPv6Slash64_WhenPartitioned_ThenTheyShareAPartition()
    {
        var first = Startup.RateLimitPartitionKey(IPAddress.Parse("2001:db8:1:2:aaaa::1"));
        var second = Startup.RateLimitPartitionKey(IPAddress.Parse("2001:db8:1:2:ffff:1:2:3"));

        Assert.Equal(first, second);
    }

    [UnitFact]
    public void GivenAddressesInDifferentIPv6Slash64s_WhenPartitioned_ThenTheyDoNot()
    {
        var first = Startup.RateLimitPartitionKey(IPAddress.Parse("2001:db8:1:2::1"));
        var second = Startup.RateLimitPartitionKey(IPAddress.Parse("2001:db8:1:3::1"));

        Assert.NotEqual(first, second);
    }

    [UnitFact]
    public void GivenIPv4Addresses_WhenPartitioned_ThenEachAddressIsItsOwnPartition()
    {
        Assert.Equal("203.0.113.7", Startup.RateLimitPartitionKey(IPAddress.Parse("203.0.113.7")));
        Assert.NotEqual(
            Startup.RateLimitPartitionKey(IPAddress.Parse("203.0.113.7")),
            Startup.RateLimitPartitionKey(IPAddress.Parse("203.0.113.8")));
    }

    [UnitFact]
    public void GivenAnIPv4MappedAddress_WhenPartitioned_ThenItCountsAsTheIPv4Caller()
    {
        Assert.Equal(
            Startup.RateLimitPartitionKey(IPAddress.Parse("203.0.113.7")),
            Startup.RateLimitPartitionKey(IPAddress.Parse("::ffff:203.0.113.7")));
    }

    [UnitFact]
    public void GivenNoAddress_WhenPartitioned_ThenAllSuchCallersShareOne()
    {
        Assert.Equal("unknown", Startup.RateLimitPartitionKey(null));
    }
}
