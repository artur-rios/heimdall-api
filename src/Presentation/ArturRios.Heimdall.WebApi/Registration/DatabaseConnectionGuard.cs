using ArturRios.Data.Relational.Core.DependencyInjection;
using ArturRios.Util.WebApi.Configuration;
using ArturRios.Util.WebApi.Security.Extensions;
using FluentValidation;
using Serilog;

namespace ArturRios.Heimdall.WebApi;

internal static class DatabaseConnectionGuard
{
    /// <summary>
    ///     Set to <c>true</c> to refuse start-up when the database connection does not require TLS,
    ///     rather than warning. Opt-in: see <see cref="WarnIfDatabaseConnectionIsNotEncrypted" />.
    /// </summary>
    private const string RequireDatabaseTlsVariable = "HEIMDALL_DATA_REQUIRE_TLS";

    /// <summary>
    ///     Warns when the database connection string does not require TLS (NFR-25, GDPR Art. 32,
    ///     LGPD Art. 46).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Everything this database holds is personal data, and Npgsql's default
    ///         <c>SSL Mode</c> is <c>Prefer</c> — which silently falls back to an unencrypted
    ///         connection when the server does not offer TLS. A deployment can therefore be sending
    ///         credentials and addresses in clear text across the network while looking correctly
    ///         configured, which is exactly the failure worth a start-up warning.
    ///     </para>
    ///     <para>
    ///         It warns by default rather than refusing to start, which is a deliberate departure
    ///         from how this codebase treats other security configuration — TH-23's email control
    ///         fails start-up in Production. The difference is that failing by default would take
    ///         down a working deployment on upgrade over a setting the operator may not control
    ///         directly.
    ///     </para>
    ///     <para>
    ///         Setting <c>HEIMDALL_DATA_REQUIRE_TLS=true</c> turns the warning into a refusal. It is
    ///         opt-in rather than the default for that reason, and it is the right setting for any
    ///         deployment that has confirmed TLS is in place: from that point, a connection string
    ///         that does not ask for encryption is a misconfiguration rather than a known state, and
    ///         starting anyway would be starting in the one condition the check exists to prevent.
    ///     </para>
    /// </remarks>
    internal static void WarnIfDatabaseConnectionIsNotEncrypted()
    {
        var connectionString = Environment.GetEnvironmentVariable("HEIMDALL_DATA_CONNECTIONSTRING");

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        var required = string.Equals(
            Environment.GetEnvironmentVariable(RequireDatabaseTlsVariable),
            "true",
            StringComparison.OrdinalIgnoreCase);

        var requiresTls = connectionString
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(part => part.Contains('='))
            .Select(part => part.Split('=', 2))
            .Where(pair => pair[0].Replace(" ", string.Empty)
                .Equals("SSLMode", StringComparison.OrdinalIgnoreCase))
            .Any(pair => pair[1].Trim() is "Require" or "VerifyCA" or "VerifyFull");

        if (requiresTls)
        {
            return;
        }

        const string explanation =
            "The database connection does not require TLS. Npgsql defaults to SSL Mode=Prefer, " +
            "which falls back to an unencrypted connection — every credential and address this " +
            "API stores would cross the network in clear text. Set SSL Mode=Require or stronger";

        if (required)
        {
            // Opted in, so the operator has said TLS is in place: a connection string that does not
            // ask for it is then a misconfiguration to fail on rather than warn about.
            throw new InvalidOperationException(
                $"{explanation}. {RequireDatabaseTlsVariable} is set, so this is refused rather than warned about.");
        }

        Log.Warning("{Explanation}", explanation);
    }
}
