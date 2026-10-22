using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Threading.RateLimiting;
using ArturRios.Data.Relational.Core.DependencyInjection;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Heimdall.WebApi.Security;
using ArturRios.Output;
using ArturRios.Util.WebApi.Configuration;
using ArturRios.Util.WebApi.Security.Extensions;
using Microsoft.AspNetCore.RateLimiting;
using Serilog;

namespace ArturRios.Heimdall.WebApi;

public static class RateLimitingConfiguration
{
    /// <summary>
    ///     Rate-limiting policy name applied via <c>[EnableRateLimiting(AuthEndpointRateLimitPolicy)]</c>
    ///     to <c>AuthController</c>'s anonymous, credential-checking endpoints. Public so the
    ///     controller and this configuration stay in sync without duplicating the literal.
    /// </summary>
    public const string AuthEndpointRateLimitPolicy = "AuthAnonymous";

    /// <summary>
    ///     Rate-limiting policy name for the endpoints that create a person with a password — each of
    ///     which derives an Argon2id hash — partitioned by the acting person rather than by address
    ///     (see <see cref="AddAccountCreationRateLimiting" />).
    /// </summary>
    public const string AccountCreationRateLimitPolicy = "AccountCreation";

    /// <summary>Requests per minute one actor may make to the account-creation endpoints.</summary>
    public const int AccountCreationPermitLimit = 30;

    /// <summary>
    ///     Length, in seconds, of the <c>AuthAnonymous</c> fixed window. One value for the limiter's
    ///     <c>Window</c> and for the <c>Retry-After</c> a rejection falls back to, so the two cannot
    ///     drift apart.
    /// </summary>
    private const int AuthEndpointRateLimitWindowSeconds = 60;

    /// <summary>
    ///     The IPv6 prefix one rate-limit partition covers (see <see cref="RateLimitPartitionKey" />).
    ///     A whole number of bytes.
    /// </summary>
    private const int RateLimitIPv6PrefixLength = 56;

    /// <summary>
    ///     The partition a caller's requests are counted in: the whole address for IPv4, and the
    ///     <c>/56</c> it belongs to for IPv6.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A /64 is the least one IPv6 subscriber is handed, and every address inside it is theirs
    ///         to use — 2<sup>64</sup> of them. Counted per address, a single caller could give every
    ///         request a fresh partition and never meet the limit at all. Counted per /64 it is no
    ///         better for the many subscribers routinely handed a /56: 256 partitions each, enough on
    ///         their own to keep the password-hash gate full. A /56 is one caller here.
    ///     </para>
    ///     <para>
    ///         Coarser is not safer: a /48 is a whole site, often many unrelated people behind one
    ///         provider, and they would share one budget of ten a minute. A flood from many unrelated
    ///         networks is not bounded here at all — that is for the edge (Threat Model TH-03). An
    ///         IPv4-mapped IPv6 address is one IPv4 caller, and is counted as that address.
    ///     </para>
    /// </remarks>
    public static string RateLimitPartitionKey(IPAddress? address)
    {
        if (address is null)
        {
            return "unknown";
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return address.ToString();
        }

        var bytes = address.GetAddressBytes();
        Array.Clear(bytes, RateLimitIPv6PrefixLength / 8, 16 - RateLimitIPv6PrefixLength / 8);

        return $"{new IPAddress(bytes)}/{RateLimitIPv6PrefixLength}";
    }

    /// <summary>
    ///     Whether a request spends the account-creation address bound: only an administrator's
    ///     person access token — the full sign-in audience, neither a two-factor challenge nor a
    ///     subject-rights token (<see cref="IdentityUserMapper" /> has already held each token's marks
    ///     to its audience) — and only the roles the four endpoints admit. Anything else is turned
    ///     away by the endpoint's own checks and must not use up a bound administrators share.
    /// </summary>
    private static bool CountsAgainstCreationAddress(IdentityUser? actor) =>
        actor is { MfaPending: false, SubjectRightsOnly: false } &&
        actor.RoleId is (int)Roles.SystemAdmin or (int)Roles.ScopeAdmin;

    /// <summary>
    ///     Throttles the three endpoints that create a person with a password (<c>POST /api/persons</c>,
    ///     <c>POST /api/scopes/{id}/persons</c>, <c>POST /api/scopes/{id}/owners</c>), and the one that invites
    ///     an existing administrator and mails them (<c>POST /api/scopes/{id}/owners/{personId}</c>), twice over:
    ///     <see cref="AccountCreationPermitLimit" /> a minute per acting person, and as many per address.
    ///     The first three derive an Argon2id hash through <c>PasswordHashGate</c>, and each needs only an
    ///     administrator's token, so without a bound one caller could keep the gate full and starve every
    ///     sign-in (Threat Model TH-03); the fourth would otherwise let one caller fill an inbox.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The actor bound is the endpoint policy. The address bound — the partition the
    ///         credential endpoints use (<see cref="AuthRateLimitPartition" />), in a namespace of its
    ///         own — is the global limiter, which ASP.NET Core applies before the endpoint policy, and
    ///         which answers "no limit" for every endpoint without that policy. Both apply to every
    ///         administrator's request, so neither can be stepped around by changing only the other
    ///         (Threat Model TH-03).
    ///     </para>
    ///     <para>
    ///         Thirty a minute is more than anyone creates by hand. The limiter runs after
    ///         authentication, so the actor is known. The global address bucket is spent only by an
    ///         administrator's person access token (<see cref="CountsAgainstCreationAddress" />): a
    ///         request without a token, or with a User's, a challenge or a subject-rights token, is
    ///         refused by the endpoint anyway and must not use up a bound administrators share.
    ///     </para>
    /// </remarks>
    private static void AddAccountCreationRateLimiting(RateLimiterOptions options, TrustedCallerOptions callers)
    {
        FixedWindowRateLimiterOptions Window(string _) => new()
        {
            PermitLimit = AccountCreationPermitLimit,
            Window = TimeSpan.FromSeconds(AuthEndpointRateLimitWindowSeconds),
            QueueLimit = 0
        };

        options.AddPolicy(AccountCreationRateLimitPolicy, httpContext =>
        {
            var actor = httpContext.GetUser<IdentityUser>();

            return RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: actor is null
                    ? AuthRateLimitPartition.KeyFor(httpContext, callers)
                    : $"actor:{actor.Id}",
                factory: Window);
        });

        options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
            httpContext.GetEndpoint()?.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName ==
            AccountCreationRateLimitPolicy && CountsAgainstCreationAddress(httpContext.GetUser<IdentityUser>())
                ? RateLimitPartition.GetFixedWindowLimiter(
                    "creation:" + AuthRateLimitPartition.KeyFor(httpContext, callers), Window)
                : RateLimitPartition.GetNoLimiter(string.Empty));
    }

    /// <summary>
    ///     Throttles the credential-checking endpoints (login, password recovery/reset, email
    ///     verification, Google sign-in, 2FA challenge verification and resend) per calling IP
    ///     address. None of these require a bearer token, so nothing else stops a caller from firing
    ///     an unbounded burst of requests at them — each login attempt alone costs a full Argon2id
    ///     verification (600 MB / 16 threads by this codebase's hashing library default), and a 2FA
    ///     email code has only 1,000,000 possible values, so an unthrottled brute force or memory/CPU
    ///     exhaustion attempt is realistic without this. The authenticated endpoints that check a
    ///     password or a second factor (2FA confirm, disable and recovery-code regeneration, and the
    ///     erasure request) carry it too: a bearer token is not the password, and without a limit
    ///     whoever holds one could test passwords or six-digit codes against them as fast as they
    ///     could send requests, outside the login lockout. The verification-link resend carries it
    ///     as well, since each request there mails the account's address. Policy name matches the
    ///     <see cref="Microsoft.AspNetCore.RateLimiting.EnableRateLimitingAttribute" /> applied to
    ///     each endpoint in <c>AuthController</c>.
    /// </summary>
    /// <remarks>
    ///     Partitioned by <see cref="HttpContext.Connection" />'s remote IP (an IPv6 caller by its /56 —
    ///     see <see cref="RateLimitPartitionKey" />), which is the caller's own
    ///     only when <see cref="TrustedProxyOptions" /> names the proxy in front of the API; otherwise
    ///     every caller shares the proxy's partition. A client system that is itself one caller to
    ///     Heimdall for many end users may instead name each user's address, with a key from
    ///     <see cref="TrustedCallerOptions" /> (see <see cref="AuthRateLimitPartition" />). Either way
    ///     this is a per-instance, defense-in-depth throttle, not a substitute for a WAF or an API
    ///     gateway's own rate limiting in front of a real deployment.
    /// </remarks>
    public static IServiceCollection AddAuthEndpointRateLimiting(this IServiceCollection services)
    {
        // Throws on a key too short to be a secret, so a misconfigured deployment stops here.
        var callers = TrustedCallerOptions.FromEnvironment();

        services.AddSingleton(callers);

        // How many, never which: the keys are secrets and the headers carrying them are not logged.
        Log.Information(
            "{Count} trusted caller key(s) configured ({Variable})",
            callers.Count, TrustedCallerOptions.KeysVariable);

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            // TH-32: a turned-away caller is told when to come back and gets the API's usual error
            // envelope, not an empty 429. The lease's own hint is used when the limiter gives one;
            // a fixed window gives none, so the window's length is the honest upper bound.
            options.OnRejected = async (context, cancellationToken) =>
            {
                var retryAfterSeconds = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter)
                    ? (int)Math.Ceiling(retryAfter.TotalSeconds)
                    : AuthEndpointRateLimitWindowSeconds;

                context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                context.HttpContext.Response.Headers.RetryAfter =
                    Math.Max(1, retryAfterSeconds).ToString(CultureInfo.InvariantCulture);

                await context.HttpContext.Response.WriteAsJsonAsync(
                    DataOutput<object?>.New.WithError(RateLimitMessages.TooManyRequests),
                    cancellationToken);
            };

            options.AddPolicy(AuthEndpointRateLimitPolicy, httpContext =>
                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: AuthRateLimitPartition.KeyFor(httpContext, callers),
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 10,
                        Window = TimeSpan.FromSeconds(AuthEndpointRateLimitWindowSeconds),
                        QueueLimit = 0
                    }));

            AddAccountCreationRateLimiting(options, callers);
        });

        return services;
    }
}
