using System.Net;

namespace ArturRios.Heimdall.WebApi.Security;

/// <summary>
///     What <see cref="SensitiveDiagnosticsGuard.Evaluate" /> decided about an instance's
///     diagnostics. A refusal is not a value: it is an exception.
/// </summary>
public enum SensitiveDiagnosticsDecision
{
    /// <summary>No sensitive diagnostic is on, so there is nothing to decide.</summary>
    Off,

    /// <summary>Diagnostics are on and no public host is configured: a developer machine or the test suite.</summary>
    Private,

    /// <summary>
    ///     Diagnostics are on, the instance serves a public host, and the operator has said so with
    ///     <see cref="SensitiveDiagnosticsGuard.AllowVariable" />. Start-up warns.
    /// </summary>
    Allowed
}

/// <summary>
///     NFR-28: refuses to start an instance that serves a public host with the diagnostics that
///     expose data — EF Core's sensitive data logging and detailed errors, and the developer
///     exception page — unless the operator sets <see cref="AllowVariable" /> to <c>true</c>.
/// </summary>
/// <remarks>
///     <para>
///         Those diagnostics are on in every environment but Production: EF writes query parameter
///         values to the log, password hashes and addresses among them, and the developer exception
///         page shows any caller a stack trace and the request. That is what a developer wants on
///         their own machine. On an instance the internet can reach it is acceptable only while the
///         instance holds made-up people (Operations &amp; Infrastructure Document §0.2), and nothing
///         in the API knew which instances those were.
///     </para>
///     <para>
///         A host is public when <see cref="PublicHostVariable" /> or <see cref="UiHostVariable" />
///         names anything but a loopback name or address. They are the variables a deployment
///         already sets to route traffic to the API, so an instance behind Traefik is recognised
///         without new configuration. Unset or empty, the instance is taken as private. The override
///         is a statement by the operator that the instance holds no real person's data, made where
///         the deployment's configuration can be read and checked; the API cannot verify it.
///     </para>
/// </remarks>
public static class SensitiveDiagnosticsGuard
{
    /// <summary>Set to <c>true</c> to start with sensitive diagnostics on a public host.</summary>
    public const string AllowVariable = "HEIMDALL_ALLOW_SENSITIVE_DIAGNOSTICS";

    /// <summary>The host name the mobile and desktop clients reach the API under.</summary>
    public const string PublicHostVariable = "PUBLIC_HOST";

    /// <summary>The web UI's host name, under which the API serves its <c>/api/</c> routes.</summary>
    public const string UiHostVariable = "UI_HOST";

    /// <summary>
    ///     Reads the host variables and the override from the environment and decides.
    /// </summary>
    /// <exception cref="InvalidOperationException">The instance must not start.</exception>
    public static SensitiveDiagnosticsDecision EvaluateFromEnvironment(
        string environmentName, bool sensitiveDataLogging, bool developerExceptionPage) =>
        Evaluate(
            environmentName,
            sensitiveDataLogging,
            developerExceptionPage,
            Environment.GetEnvironmentVariable(PublicHostVariable),
            Environment.GetEnvironmentVariable(UiHostVariable),
            Environment.GetEnvironmentVariable(AllowVariable));

    /// <summary>
    ///     Decides whether an instance may start with the given diagnostics. Split from
    ///     <see cref="EvaluateFromEnvironment" /> so it can be tested without mutating the process
    ///     environment the functional suite's hosts read.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    ///     A diagnostic is on, a host is public, and <paramref name="allow" /> is not <c>true</c>.
    /// </exception>
    public static SensitiveDiagnosticsDecision Evaluate(
        string environmentName,
        bool sensitiveDataLogging,
        bool developerExceptionPage,
        string? publicHost,
        string? uiHost,
        string? allow)
    {
        if (!sensitiveDataLogging && !developerExceptionPage)
        {
            return SensitiveDiagnosticsDecision.Off;
        }

        var publicHosts = new[] { (PublicHostVariable, publicHost), (UiHostVariable, uiHost) }
            .Where(host => IsPublicHost(host.Item2))
            .Select(host => $"{host.Item1}={host.Item2!.Trim()}")
            .ToList();

        if (publicHosts.Count == 0)
        {
            return SensitiveDiagnosticsDecision.Private;
        }

        if (string.Equals(allow?.Trim(), "true", StringComparison.OrdinalIgnoreCase))
        {
            return SensitiveDiagnosticsDecision.Allowed;
        }

        var enabled = new List<string>();

        if (sensitiveDataLogging)
        {
            enabled.Add("EF Core sensitive data logging and detailed errors");
        }

        if (developerExceptionPage)
        {
            enabled.Add("the developer exception page");
        }

        throw new InvalidOperationException(
            $"The {environmentName} environment turns on {string.Join(" and ", enabled)}, and this " +
            $"instance serves a public host ({string.Join(", ", publicHosts)}). They put query " +
            "parameter values, password hashes and addresses among them, in the log and show stack " +
            "traces to any caller. Refusing to start. Run the instance as Production, or set " +
            $"{AllowVariable}=true to state that it holds made-up people only.");
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
