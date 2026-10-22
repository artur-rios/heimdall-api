using System.Net;
using ArturRios.Util.Test.Attributes;

namespace ArturRios.Heimdall.WebApi.Tests;

// Unit tests for the partition the credential-endpoint rate limiter counts a caller in. An IPv6
// subscriber holds at least a /64 and is routinely handed a /56, so counting per address — or per
// /64 — would let one caller spread requests over many partitions and never meet the limit.
public class RateLimitPartitionKeyTests
{
    [UnitFact]
    public void GivenTwoAddressesInOneIPv6Slash64_WhenPartitioned_ThenTheyShareAPartition()
    {
        // Given / When
        var first = RateLimitingConfiguration.RateLimitPartitionKey(IPAddress.Parse("2001:db8:1:2:aaaa::1"));
        var second = RateLimitingConfiguration.RateLimitPartitionKey(IPAddress.Parse("2001:db8:1:2:ffff:1:2:3"));

        // Then
        Assert.Equal(first, second);
    }

    [UnitFact]
    public void GivenAddressesInDifferentSlash64sOfOneIPv6Slash56_WhenPartitioned_ThenTheyShareAPartition()
    {
        // Given the first and the last /64 of one /56 — 256 partitions to one subscriber, if the
        // limiter counted per /64
        var first = RateLimitingConfiguration.RateLimitPartitionKey(IPAddress.Parse("2001:db8:1:200::1"));
        var last = RateLimitingConfiguration.RateLimitPartitionKey(IPAddress.Parse("2001:db8:1:2ff:ffff:ffff:ffff:ffff"));

        // Then
        Assert.Equal(first, last);
    }

    [UnitFact]
    public void GivenAnIPv6Address_WhenPartitioned_ThenTheKeyNamesItsSlash56()
    {
        // Given / When
        var key = RateLimitingConfiguration.RateLimitPartitionKey(IPAddress.Parse("2001:db8:1:2ab:cd::1"));

        // Then
        Assert.Equal("2001:db8:1:200::/56", key);
    }

    [UnitFact]
    public void GivenAddressesInDifferentIPv6Slash56s_WhenPartitioned_ThenTheyDoNot()
    {
        // Given neighbouring /56s, which are two subscribers
        var first = RateLimitingConfiguration.RateLimitPartitionKey(IPAddress.Parse("2001:db8:1:2ff::1"));
        var second = RateLimitingConfiguration.RateLimitPartitionKey(IPAddress.Parse("2001:db8:1:300::1"));

        // Then
        Assert.NotEqual(first, second);
    }

    [UnitFact]
    public void GivenIPv4Addresses_WhenPartitioned_ThenEachAddressIsItsOwnPartition()
    {
        Assert.Equal("203.0.113.7", RateLimitingConfiguration.RateLimitPartitionKey(IPAddress.Parse("203.0.113.7")));
        Assert.NotEqual(
            RateLimitingConfiguration.RateLimitPartitionKey(IPAddress.Parse("203.0.113.7")),
            RateLimitingConfiguration.RateLimitPartitionKey(IPAddress.Parse("203.0.113.8")));
    }

    [UnitFact]
    public void GivenAnIPv4MappedAddress_WhenPartitioned_ThenItCountsAsTheIPv4Caller()
    {
        Assert.Equal(
            RateLimitingConfiguration.RateLimitPartitionKey(IPAddress.Parse("203.0.113.7")),
            RateLimitingConfiguration.RateLimitPartitionKey(IPAddress.Parse("::ffff:203.0.113.7")));
    }

    [UnitFact]
    public void GivenNoAddress_WhenPartitioned_ThenAllSuchCallersShareOne()
    {
        Assert.Equal("unknown", RateLimitingConfiguration.RateLimitPartitionKey(null));
    }
}
