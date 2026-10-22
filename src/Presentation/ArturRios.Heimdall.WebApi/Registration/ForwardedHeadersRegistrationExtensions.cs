using ArturRios.Data.Relational.Core.DependencyInjection;
using ArturRios.Heimdall.WebApi.Security;
using ArturRios.Util.WebApi.Configuration;
using ArturRios.Util.WebApi.Security.Extensions;
using Serilog;

namespace ArturRios.Heimdall.WebApi;

public static class ForwardedHeadersRegistrationExtensions
{
    /// <summary>
    ///     Believes <c>X-Forwarded-For</c> and <c>X-Forwarded-Proto</c> from the proxies
    ///     <c>HEIMDALL_TRUSTED_PROXIES</c> names, and from no one else — see
    ///     <see cref="TrustedProxyOptions" />. Applied by <c>UseForwardedHeaders</c>, which runs
    ///     ahead of everything that reads the client address: the rate limiter, and the request log.
    /// </summary>
    public static IServiceCollection AddTrustedForwardedHeaders(this IServiceCollection services)
    {
        var proxies = TrustedProxyOptions.FromEnvironment();

        if (proxies.InvalidEntries.Count > 0)
        {
            Log.Warning(
                "Ignoring unusable {Variable} entries {Entries}; they are neither an address nor a CIDR network",
                TrustedProxyOptions.ProxiesVariable, proxies.InvalidEntries);
        }

        if (!proxies.Configured)
        {
            Log.Warning(
                "No trusted proxy is configured ({Variable}); behind a reverse proxy every caller will " +
                "be rate limited and logged under the proxy's address",
                TrustedProxyOptions.ProxiesVariable);

            return services;
        }

        Log.Information(
            "Trusting forwarded headers from {Proxies} and {Networks}",
            proxies.Proxies.Select(proxy => proxy.ToString()),
            proxies.Networks.Select(network => network.ToString()));

        services.Configure<ForwardedHeadersOptions>(proxies.Apply);

        return services;
    }
}
