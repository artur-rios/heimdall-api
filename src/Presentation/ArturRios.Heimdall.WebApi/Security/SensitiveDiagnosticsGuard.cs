using System.Net;

namespace ArturRios.Heimdall.WebApi.Security;

/// <summary>
///     How <see cref="SensitiveDiagnosticsGuard.Evaluate" /> settled an instance's diagnostics.
/// </summary>
public enum SensitiveDiagnosticsDecision
{
    /// <summary>The environment turns no sensitive diagnostic on, so there is nothing to decide.</summary>
    Off,

    /// <summary>Diagnostics are on and nothing names a public host: a developer machine or the test suite.</summary>
    Private,

    /// <summary>
    ///     The instance serves a public host and the operator has set
    ///     <see cref="SensitiveDiagnosticsGuard.AllowVariable" />, so the diagnostics stay on. Start-up
    ///     warns.
    /// </summary>
    Allowed,

    /// <summary>
    ///     The instance serves a public host without the override, so every sensitive diagnostic is
    ///     turned off. Start-up warns.
    /// </summary>
    Suppressed
}

/// <summary>
///     The one decision about the diagnostics that expose data, read by everything that turns one
///     on: EF Core's sensitive data logging and detailed errors (<see cref="DataLogging" />) and the
///     developer exception page (<see cref="DeveloperExceptionPage" />).
/// </summary>
/// <param name="DataLogging">Whether EF Core logs parameter values and adds detail to its errors.</param>
/// <param name="DeveloperExceptionPage">Whether the developer exception page is served.</param>
/// <param name="Decision">How the two were settled.</param>
/// <param name="PublicHosts">
///     The settings that named a public host, as <c>VARIABLE=host</c>, for the start-up warning.
///     Host names only; no other part of a value.
/// </param>
public sealed record SensitiveDiagnostics(
    bool DataLogging,
    bool DeveloperExceptionPage,
    SensitiveDiagnosticsDecision Decision,
    IReadOnlyList<string> PublicHosts)
{
    /// <summary>Everything off: the safe value before anything has been decided.</summary>
    public static readonly SensitiveDiagnostics None = new(false, false, SensitiveDiagnosticsDecision.Off, []);
}

/// <summary>
///     The configuration that says where an instance is reached from: the host names Traefik routes
///     to it, and the public addresses the API already writes into mail and accepts browser calls
///     from.
/// </summary>
public sealed record PublicHostSettings(
    string? PublicHost,
    string? UiHost,
    string? VerificationUrl,
    string? PasswordResetUrl,
    string? CorsAllowedOrigins)
{
    /// <summary>The host name the mobile and desktop clients reach the API under.</summary>
    public const string PublicHostVariable = "PUBLIC_HOST";

    /// <summary>The web UI's host name, under which the API serves its <c>/api/</c> routes.</summary>
    public const string UiHostVariable = "UI_HOST";

    /// <summary>Where a verification link points (the web UI).</summary>
    public const string VerificationUrlVariable = "HEIMDALL_EMAIL_VERIFICATION_URL";

    /// <summary>Where a password-reset link points (the web UI).</summary>
    public const string PasswordResetUrlVariable = "HEIMDALL_PASSWORD_RESET_URL";

    /// <summary>The browser origins allowed to call the API, comma-separated.</summary>
    public const string CorsAllowedOriginsVariable = "HEIMDALL_CORS_ALLOWED_ORIGINS";

    public static PublicHostSettings FromEnvironment() => new(
        Environment.GetEnvironmentVariable(PublicHostVariable),
        Environment.GetEnvironmentVariable(UiHostVariable),
        Environment.GetEnvironmentVariable(VerificationUrlVariable),
        Environment.GetEnvironmentVariable(PasswordResetUrlVariable),
        Environment.GetEnvironmentVariable(CorsAllowedOriginsVariable));

    /// <summary>
    ///     Every setting that names a public host, as <c>VARIABLE=host</c>. The two host variables are
    ///     read as host names; the URLs and the origins only when they parse as absolute
    ///     <c>http</c> or <c>https</c> URLs, so a template placeholder is not taken for a host.
    /// </summary>
    public IReadOnlyList<string> PublicHosts()
    {
        var found = new List<string>();

        foreach (var (variable, value) in new[] { (PublicHostVariable, PublicHost), (UiHostVariable, UiHost) })
        {
            if (SensitiveDiagnosticsGuard.IsPublicHost(value))
            {
                found.Add($"{variable}={value!.Trim()}");
            }
        }

        var urls = new[] { (VerificationUrlVariable, VerificationUrl), (PasswordResetUrlVariable, PasswordResetUrl) }
            .Concat((CorsAllowedOrigins ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(origin => (CorsAllowedOriginsVariable, (string?)origin)));

        foreach (var (variable, value) in urls)
        {
            if (Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
                SensitiveDiagnosticsGuard.IsPublicHost(uri.Host))
            {
                found.Add($"{variable}={uri.Host}");
            }
        }

        return found.Distinct().ToList();
    }
}

/// <summary>
///     NFR-28: decides the diagnostics that expose data — EF Core's sensitive data logging and
///     detailed errors, and the developer exception page. On an instance that serves a public host
///     they are turned off, unless the operator sets <see cref="AllowVariable" /> to <c>true</c>.
/// </summary>
/// <remarks>
///     <para>
///         Those diagnostics are on in every environment but Production: EF writes query parameter
///         values to the log, and the developer exception page answers a failed request with its
///         details. That is what a developer wants on their own machine. On an instance reachable from
///         outside they stay on only when the operator says the instance holds made-up people
///         (Operations &amp; Infrastructure Document §0.2).
///     </para>
///     <para>
///         The instance never refuses to start over this: it starts with the diagnostics off and
///         says so, so an environment that is deployed automatically keeps running. A host is public
///         when any setting in <see cref="PublicHostSettings" /> names anything but a loopback name
///         or address. With nothing set, the instance is taken as private.
///     </para>
/// </remarks>
public static class SensitiveDiagnosticsGuard
{
    /// <summary>Set to <c>true</c> to keep sensitive diagnostics on with a public host.</summary>
    public const string AllowVariable = "HEIMDALL_ALLOW_SENSITIVE_DIAGNOSTICS";

    /// <summary>Reads the host settings and the override from the environment and decides.</summary>
    public static SensitiveDiagnostics EvaluateFromEnvironment(bool dataLogging, bool developerExceptionPage) =>
        Evaluate(
            dataLogging,
            developerExceptionPage,
            PublicHostSettings.FromEnvironment(),
            Environment.GetEnvironmentVariable(AllowVariable));

    /// <summary>
    ///     Decides what an instance runs with, given what its environment asks for. Split from
    ///     <see cref="EvaluateFromEnvironment" /> so it can be tested without mutating the process
    ///     environment the functional suite's hosts read.
    /// </summary>
    public static SensitiveDiagnostics Evaluate(
        bool dataLogging, bool developerExceptionPage, PublicHostSettings hosts, string? allow)
    {
        if (!dataLogging && !developerExceptionPage)
        {
            return SensitiveDiagnostics.None;
        }

        var publicHosts = hosts.PublicHosts();

        if (publicHosts.Count == 0)
        {
            return new SensitiveDiagnostics(dataLogging, developerExceptionPage, SensitiveDiagnosticsDecision.Private, []);
        }

        return string.Equals(allow?.Trim(), "true", StringComparison.OrdinalIgnoreCase)
            ? new SensitiveDiagnostics(dataLogging, developerExceptionPage, SensitiveDiagnosticsDecision.Allowed, publicHosts)
            : new SensitiveDiagnostics(false, false, SensitiveDiagnosticsDecision.Suppressed, publicHosts);
    }

    /// <summary>
    ///     Whether a host variable names a host other than this machine. Empty is not public;
    ///     <c>localhost</c>, a name under <c>.localhost</c> and a loopback address are not public;
    ///     anything else is. A scheme, a port and a path are ignored, so a URL is read as its host.
    /// </summary>
    public static bool IsPublicHost(string? value)
    {
        var host = (value ?? string.Empty).Trim();

        if (host.Length == 0)
        {
            return false;
        }

        var scheme = host.IndexOf("://", StringComparison.Ordinal);

        if (scheme >= 0)
        {
            host = host[(scheme + 3)..];
        }

        var path = host.IndexOf('/');

        if (path >= 0)
        {
            host = host[..path];
        }

        if (host.StartsWith('['))
        {
            // A bracketed IPv6 address, with or without a port after it.
            var close = host.IndexOf(']');
            host = close > 0 ? host[1..close] : host[1..];
        }
        else if (host.Count(character => character == ':') == 1)
        {
            // host:port. More than one colon is a bare IPv6 address, which has no port.
            host = host[..host.IndexOf(':')];
        }

        host = host.TrimEnd('.');

        if (host.Length == 0)
        {
            return false;
        }

        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !(IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address));
    }
}
