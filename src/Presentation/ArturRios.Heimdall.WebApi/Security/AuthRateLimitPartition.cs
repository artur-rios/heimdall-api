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
///     probe for the keys by their effect on anything but the caller's own bucket. Neither header value
///     is logged.
/// </remarks>
public static class AuthRateLimitPartition
{
    /// <summary>Header carrying one of the configured <see cref="TrustedCallerOptions" /> keys.</summary>
    public const string CallerKeyHeader = "X-Heimdall-Caller-Key";

    /// <summary>Header carrying the address of the end user the trusted caller is acting for.</summary>
    public const string ClientAddressHeader = "X-Heimdall-Client-Address";

    public static string KeyFor(HttpContext context, TrustedCallerOptions callers)
    {
        if (callers.Configured &&
            TryReadSingle(context.Request.Headers[CallerKeyHeader], out var key) &&
            callers.Matches(key) &&
            TryReadSingle(context.Request.Headers[ClientAddressHeader], out var address) &&
            IPAddress.TryParse(address, out var clientAddress))
        {
            return Startup.RateLimitPartitionKey(clientAddress);
        }

        return Startup.RateLimitPartitionKey(context.Connection.RemoteIpAddress);
    }

    // A header sent twice is ambiguous, and the safe reading of an ambiguous credential is none.
    private static bool TryReadSingle(Microsoft.Extensions.Primitives.StringValues values, out string value)
    {
        value = values.Count == 1 ? values[0] ?? string.Empty : string.Empty;

        return value.Length > 0;
    }
}
