using System.Net;

namespace ArturRios.Heimdall.WebApi.Security;

/// <summary>
///     Chooses the rate-limit partition an anonymous-auth request is counted in: the address of the
///     connection, unless a trusted client system names the address of the end user it is acting for.
/// </summary>
/// <remarks>
///     The decision is a pure function of the request and <see cref="TrustedCallerOptions" /> so it can
///     be tested without a host. It never fails a request: a wrong or missing key, an unusable address,
///     or no configured keys all fall back to the connection address, so the headers cannot be used to
///     probe for the keys by their effect on anything but the caller's own bucket. A named address is
///     counted under <see cref="NamedAddressPrefix" /> so it can never be the bucket of a real
///     connection from that address. Neither header value is logged.
/// </remarks>
public static class AuthRateLimitPartition
{
    /// <summary>Header carrying one of the configured <see cref="TrustedCallerOptions" /> keys.</summary>
    public const string CallerKeyHeader = "X-Heimdall-Caller-Key";

    /// <summary>Header carrying the address of the end user the trusted caller is acting for.</summary>
    public const string ClientAddressHeader = "X-Heimdall-Client-Address";

    /// <summary>
    ///     Prefix of every partition a trusted caller names. A direct connection keeps the unprefixed
    ///     key, so the two never share a bucket.
    /// </summary>
    public const string NamedAddressPrefix = "caller:";

    public static string KeyFor(HttpContext context, TrustedCallerOptions callers)
    {
        if (callers.Configured &&
            TryReadSingle(context.Request.Headers[CallerKeyHeader], out var key) &&
            callers.Matches(key) &&
            TryReadSingle(context.Request.Headers[ClientAddressHeader], out var address) &&
            IPAddress.TryParse(address, out var clientAddress))
        {
            // Its own namespace: a named address is counted apart from a direct connection from the
            // same address, so a key holder cannot spend a real caller's budget by naming them.
            return NamedAddressPrefix + RateLimitingConfiguration.RateLimitPartitionKey(clientAddress);
        }

        return RateLimitingConfiguration.RateLimitPartitionKey(context.Connection.RemoteIpAddress);
    }

    // A header sent twice is ambiguous, and the safe reading of an ambiguous credential is none.
    private static bool TryReadSingle(Microsoft.Extensions.Primitives.StringValues values, out string value)
    {
        value = values.Count == 1 ? values[0] ?? string.Empty : string.Empty;

        return value.Length > 0;
    }
}
