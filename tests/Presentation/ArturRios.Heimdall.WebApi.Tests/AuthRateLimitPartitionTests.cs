using System.Net;
using ArturRios.Heimdall.WebApi.Security;
using ArturRios.Util.Test.Attributes;
using Microsoft.AspNetCore.Http;

namespace ArturRios.Heimdall.WebApi.Tests;

// Unit tests for the decision of which rate-limit partition an anonymous-auth request is counted in.
// A client system such as Fortuna's API reaches Heimdall from one address for every end user, so it
// may name the user's own address — but only with a key Heimdall was configured to trust. Anything
// short of that falls back silently to the connection address: the headers never fail a request and
// never grant anything beyond choosing the bucket.
public class AuthRateLimitPartitionTests
{
    private const string KeyA = "caller-key-A-0123456789abcdefghijklmn";
    private const string KeyB = "caller-key-B-0123456789abcdefghijklmn";
    private const string Connection = "172.18.0.9";

    private static readonly string ConnectionPartition = Startup.RateLimitPartitionKey(IPAddress.Parse(Connection));

    private static DefaultHttpContext Context(string? key = null, string? clientAddress = null)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(Connection);

        if (key is not null)
        {
            context.Request.Headers[AuthRateLimitPartition.CallerKeyHeader] = key;
        }

        if (clientAddress is not null)
        {
            context.Request.Headers[AuthRateLimitPartition.ClientAddressHeader] = clientAddress;
        }

        return context;
    }

    private static TrustedCallerOptions Callers(string? keys = $"{KeyA},{KeyB}") => TrustedCallerOptions.Parse(keys);

    [UnitFact]
    public void GivenAValidKeyAndAnIPv4Address_WhenPartitioning_ThenThatAddressIsTheKey()
    {
        var key = AuthRateLimitPartition.KeyFor(Context(KeyA, "203.0.113.7"), Callers());

        Assert.Equal("caller:203.0.113.7", key);
    }

    [UnitFact]
    public void GivenAValidKeyAndAnIPv6Address_WhenPartitioning_ThenItsSlash64IsTheKey()
    {
        var key = AuthRateLimitPartition.KeyFor(Context(KeyA, "2001:db8:1:2:aaaa::1"), Callers());

        Assert.Equal("caller:2001:db8:1:2::/64", key);
        Assert.Equal(key, AuthRateLimitPartition.KeyFor(Context(KeyA, "2001:db8:1:2:ffff:1:2:3"), Callers()));
    }

    [UnitFact]
    public void GivenAValidKeyAndAnIPv4MappedAddress_WhenPartitioning_ThenItCountsAsTheIPv4Caller()
    {
        var key = AuthRateLimitPartition.KeyFor(Context(KeyA, "::ffff:203.0.113.7"), Callers());

        Assert.Equal("caller:203.0.113.7", key);
    }

    [UnitFact]
    public void GivenTheSecondConfiguredKey_WhenPartitioning_ThenItWorksToo()
    {
        var key = AuthRateLimitPartition.KeyFor(Context(KeyB, "203.0.113.8"), Callers());

        Assert.Equal("caller:203.0.113.8", key);
    }

    [UnitFact]
    public void GivenANamedAddress_WhenPartitioning_ThenItNeverSharesABucketWithADirectConnectionFromThatAddress()
    {
        // A key holder naming a real caller's address must not be able to spend that caller's own
        // budget: a direct connection from the address keeps the unprefixed key.
        var direct = new DefaultHttpContext();
        direct.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.7");

        var named = AuthRateLimitPartition.KeyFor(Context(KeyA, "203.0.113.7"), Callers());
        var directKey = AuthRateLimitPartition.KeyFor(direct, Callers());

        Assert.Equal("203.0.113.7", directKey);
        Assert.NotEqual(directKey, named);
        Assert.StartsWith(AuthRateLimitPartition.NamedAddressPrefix, named);
    }

    [UnitFact]
    public void GivenANamedIPv6Address_WhenPartitioning_ThenItNeverSharesABucketWithADirectSlash64()
    {
        var direct = new DefaultHttpContext();
        direct.Connection.RemoteIpAddress = IPAddress.Parse("2001:db8:1:2::5");

        var named = AuthRateLimitPartition.KeyFor(Context(KeyA, "2001:db8:1:2::5"), Callers());
        var directKey = AuthRateLimitPartition.KeyFor(direct, Callers());

        Assert.Equal("2001:db8:1:2::/64", directKey);
        Assert.NotEqual(directKey, named);
    }

    [UnitFact]
    public void GivenAWrongKey_WhenPartitioning_ThenTheConnectionAddressIsTheKey()
    {
        var key = AuthRateLimitPartition.KeyFor(
            Context("caller-key-X-0123456789abcdefghijklmn", "203.0.113.7"), Callers());

        Assert.Equal(ConnectionPartition, key);
    }

    [UnitFact]
    public void GivenAKeyThatOnlyStartsLikeAConfiguredOne_WhenPartitioning_ThenTheConnectionAddressIsTheKey()
    {
        var key = AuthRateLimitPartition.KeyFor(Context(KeyA[..^1], "203.0.113.7"), Callers());

        Assert.Equal(ConnectionPartition, key);
    }

    [UnitFact]
    public void GivenNoKey_WhenPartitioning_ThenTheConnectionAddressIsTheKey()
    {
        var key = AuthRateLimitPartition.KeyFor(Context(clientAddress: "203.0.113.7"), Callers());

        Assert.Equal(ConnectionPartition, key);
    }

    [UnitFact]
    public void GivenAValidKeyAndNoAddress_WhenPartitioning_ThenTheConnectionAddressIsTheKey()
    {
        var key = AuthRateLimitPartition.KeyFor(Context(KeyA), Callers());

        Assert.Equal(ConnectionPartition, key);
    }

    [UnitTheory]
    [InlineData("not-an-address")]
    [InlineData("")]
    [InlineData("203.0.113.7, 203.0.113.8")]
    [InlineData("203.0.113.256")]
    public void GivenAValidKeyAndAnUnusableAddress_WhenPartitioning_ThenTheConnectionAddressIsTheKey(string address)
    {
        var key = AuthRateLimitPartition.KeyFor(Context(KeyA, address), Callers());

        Assert.Equal(ConnectionPartition, key);
    }

    [UnitFact]
    public void GivenNoConfiguredKeys_WhenPartitioning_ThenTheConnectionAddressIsTheKeyEvenWithHeaders()
    {
        var key = AuthRateLimitPartition.KeyFor(Context(KeyA, "203.0.113.7"), Callers(keys: null));

        Assert.Equal(ConnectionPartition, key);
    }

    [UnitFact]
    public void GivenTheKeyHeaderSentTwice_WhenPartitioning_ThenTheConnectionAddressIsTheKey()
    {
        // Two header lines are ambiguous, and the safe reading of an ambiguous credential is none.
        var context = Context(clientAddress: "203.0.113.7");
        context.Request.Headers[AuthRateLimitPartition.CallerKeyHeader] = new[] { KeyA, KeyA };

        Assert.Equal(ConnectionPartition, AuthRateLimitPartition.KeyFor(context, Callers()));
    }

    [UnitFact]
    public void GivenNoConnectionAddressAndNoKey_WhenPartitioning_ThenAllSuchCallersShareOne()
    {
        var context = new DefaultHttpContext();

        Assert.Equal("unknown", AuthRateLimitPartition.KeyFor(context, Callers()));
    }
}
