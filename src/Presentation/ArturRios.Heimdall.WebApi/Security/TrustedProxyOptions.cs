using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace ArturRios.Heimdall.WebApi.Security;

/// <summary>
///     The reverse proxies allowed to tell the API who its caller is, read once at start-up from
///     <c>HEIMDALL_TRUSTED_PROXIES</c>: a comma-separated list of addresses (<c>172.18.0.2</c>) and
///     CIDR networks (<c>172.18.0.0/16</c>).
/// </summary>
/// <remarks>
///     <para>
///         In production the API runs behind Traefik, so every connection's remote address is
///         Traefik's. The caller's own address arrives in <c>X-Forwarded-For</c> and the original
///         scheme in <c>X-Forwarded-Proto</c>, and ASP.NET Core's forwarded-headers middleware puts
///         them back on the connection. Both the rate limiter's partition key and the client address
///         <c>TraceActivityMiddleware</c> logs read the connection, so without this every caller
///         shares one rate-limit partition and every log line names the proxy.
///     </para>
///     <para>
///         The headers are only believed when the connection comes from a proxy listed here. Any
///         client can send <c>X-Forwarded-For</c>; trusting it from everyone would let a caller pick
///         the address it is rate limited and logged under. For the same reason only one hop is
///         read (the middleware's default <c>ForwardLimit</c> of 1): Traefik appends the address it
///         saw to whatever the client sent, so the rightmost entry is the only one Traefik vouches
///         for.
///     </para>
///     <para>
///         Behind Cloudflare that rightmost entry is an edge address, which unrelated visitors share.
///         Traefik keeps the header only from Cloudflare's ranges, and Cloudflare appends the visitor
///         to whatever the visitor sent, so the header reads <c>[forged,] visitor, edge</c> and the
///         entry just left of the edge is the one vouched for. <see cref="CloudflareVariable" /> says
///         so, and <see cref="KeepOnlyTheVisitor" /> leaves that entry alone in the header before the
///         middleware reads it. From anywhere else Traefik discards what the client sent and writes
///         the connection's address alone, so a client reaching the machine directly is one entry and
///         is still counted as itself.
///     </para>
///     <para>
///         Listing Cloudflare's ranges here and raising <c>ForwardLimit</c> would be the middleware's
///         own way to do it, and is not safe in this API: the middleware runs twice (see
///         <c>EdgePipeline</c>), so a visitor whose own address is inside those ranges would have the
///         second pass read the forged entry to its left. It would also be one more copy of a list
///         that changes, going stale without a sound.
///     </para>
///     <para>
///         Unset, nothing is trusted beyond loopback (ASP.NET Core's default), and the headers are
///         not processed at all — the safe reading of a deployment nobody has described.
///     </para>
/// </remarks>
public sealed class TrustedProxyOptions
{
    /// <summary>Comma-separated addresses and CIDR networks of the trusted reverse proxies.</summary>
    public const string ProxiesVariable = "HEIMDALL_TRUSTED_PROXIES";

    /// <summary>Set to <c>true</c> when Cloudflare's proxy stands in front of the trusted proxy.</summary>
    public const string CloudflareVariable = "HEIMDALL_CLOUDFLARE_PROXIED";

    /// <summary>Trusted proxies given as single addresses.</summary>
    public IReadOnlyList<IPAddress> Proxies { get; private init; } = [];

    /// <summary>Trusted proxies given as CIDR networks.</summary>
    public IReadOnlyList<System.Net.IPNetwork> Networks { get; private init; } = [];

    /// <summary>
    ///     Entries that were neither an address nor a network, and were ignored. Kept so start-up can
    ///     say so, instead of silently trusting less than the operator asked for.
    /// </summary>
    public IReadOnlyList<string> InvalidEntries { get; private init; } = [];

    /// <summary>
    ///     Whether <see cref="CloudflareVariable" /> says Cloudflare's proxy is in front of the trusted
    ///     proxy, which moves the visitor one entry to the left in <c>X-Forwarded-For</c>.
    /// </summary>
    public bool BehindCloudflare { get; private init; }

    /// <summary>Whether any proxy is trusted, and so whether forwarded headers are processed.</summary>
    public bool Configured => Proxies.Count > 0 || Networks.Count > 0;

    public static TrustedProxyOptions FromEnvironment() => Parse(
        Environment.GetEnvironmentVariable(ProxiesVariable),
        Environment.GetEnvironmentVariable(CloudflareVariable));

    /// <summary>
    ///     Interprets a <see cref="ProxiesVariable" /> value. Split from <see cref="FromEnvironment" />
    ///     so it can be tested without mutating the process environment the functional suite's hosts
    ///     read.
    /// </summary>
    public static TrustedProxyOptions Parse(string? value, string? cloudflareProxied = null)
    {
        List<IPAddress> proxies = [];
        List<System.Net.IPNetwork> networks = [];
        List<string> invalid = [];

        var entries = (value ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var entry in entries)
        {
            if (entry.Contains('/') && System.Net.IPNetwork.TryParse(entry, out var network))
            {
                networks.Add(network);
            }
            else if (!entry.Contains('/') && IPAddress.TryParse(entry, out var address))
            {
                proxies.Add(address);
            }
            else
            {
                invalid.Add(entry);
            }
        }

        return new TrustedProxyOptions
        {
            Proxies = proxies,
            Networks = networks,
            InvalidEntries = invalid,
            BehindCloudflare = string.Equals(cloudflareProxied?.Trim(), "true", StringComparison.OrdinalIgnoreCase)
        };
    }

    /// <summary>
    ///     Reduces <c>X-Forwarded-For</c> to the entry Cloudflare appended, the one to the left of the
    ///     address Traefik saw. A header of one entry is left as it is: it is not from Cloudflare.
    /// </summary>
    /// <remarks>
    ///     Everything further left came from the visitor, so it is removed rather than left for a
    ///     later pass of the middleware to find. The middleware still decides whether the header is
    ///     believed at all, from the connection's address.
    /// </remarks>
    public static void KeepOnlyTheVisitor(IHeaderDictionary headers)
    {
        var entries = headers.GetCommaSeparatedValues(ForwardedHeadersDefaults.XForwardedForHeaderName);

        if (entries.Length > 1)
        {
            headers[ForwardedHeadersDefaults.XForwardedForHeaderName] = entries[^2];
        }
    }

    /// <summary>
    ///     Configures the forwarded-headers middleware to read the client address and scheme from the
    ///     trusted proxies, and from them only. ASP.NET Core's loopback defaults are replaced rather
    ///     than added to: the list says exactly who is trusted.
    /// </summary>
    /// <remarks>
    ///     <c>X-Forwarded-Host</c> is deliberately not read. Traefik forwards the client's own
    ///     <c>Host</c> already, and nothing in the API decides anything on the host name — the
    ///     metrics endpoint is told apart by port precisely because the host is the caller's choice.
    /// </remarks>
    public void Apply(ForwardedHeadersOptions options)
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;

        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();

        foreach (var proxy in Proxies)
        {
            options.KnownProxies.Add(proxy);
        }

        foreach (var network in Networks)
        {
            options.KnownIPNetworks.Add(network);
        }
    }
}
