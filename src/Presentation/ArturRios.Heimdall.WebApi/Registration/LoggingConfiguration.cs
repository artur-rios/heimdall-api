using ArturRios.Data.Relational.Core.DependencyInjection;
using ArturRios.Heimdall.Shared.Retention;
using ArturRios.Heimdall.WebApi.Security;
using ArturRios.Messaging.Email;
using ArturRios.Util.WebApi.Configuration;
using ArturRios.Util.WebApi.Security.Extensions;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Json;

namespace ArturRios.Heimdall.WebApi;

internal static class LoggingConfiguration
{
    private const string LogDirectoryEnvironmentVariable = "HEIMDALL_LOG_DIRECTORY";
    private const string DefaultLogDirectory = "logs";

    /// <summary>
    ///     Configures the console and file sinks, and bounds how long the files live (NFR-22).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The year/month directory layout is gone, deliberately.</b> The sink used to be
    ///         wrapped in <c>WriteTo.Map</c> keyed on <c>yyyy/MM</c>, which created a <em>new sink
    ///         per month</em>. A retention limit bounds the files within one sink, so it would have
    ///         bounded each month's directory and never removed a month — the files from the first
    ///         month a deployment ran were still on disk years later. A flat rolling sink with
    ///         <c>retainedFileTimeLimit</c> is the shape where the limit actually applies.
    ///     </para>
    ///     <para>
    ///         Read straight from the environment rather than through the configured options,
    ///         because this runs before <c>LoadConfiguration</c> — the same reason the log directory
    ///         is read this way. An unusable value falls back to the documented default, silently
    ///         here because there is no logger yet to warn with; <c>AddDataRetention</c> reads the
    ///         same variable later and does warn.
    ///     </para>
    ///     <para>
    ///         <b>Every request's client IP address is logged</b>, by Util.WebApi's
    ///         <c>TraceActivityMiddleware</c>, as the address the request came from — the real caller's
    ///         once <see cref="TrustedProxyOptions" /> names the proxy in front of the API. It is
    ///         security telemetry, kept for the same period as the rest of the log, and the Data
    ///         Retention Schedule and the Privacy Notice say so.
    ///     </para>
    ///     <para>
    ///         <b><c>MailgunEmailService</c> is silenced.</b> The messaging library logs every send
    ///         with the recipient's address in the clear — "Sending e-mail to …" and "accepted the
    ///         message for …" at Information, the same on a rejection at Error — which is exactly what
    ///         NFR-22 forbids, and this codebase's own redaction cannot reach a library's templates.
    ///         Nothing is lost: <c>MailgunSender</c> already logs every outcome, refusal and exception
    ///         included, under a <c>LogSafeEmail</c> reference.
    ///     </para>
    /// </remarks>
    internal static void Configure()
    {
        var logDirectory = Environment.GetEnvironmentVariable(LogDirectoryEnvironmentVariable)
                           ?? DefaultLogDirectory;

        var retention = DataRetentionOptions.FromEnvironment().LogRetention;

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Override(typeof(MailgunEmailService).FullName!, LogEventLevel.Fatal)
            .WriteTo.Console(new JsonFormatter())
            .WriteTo.File(
                new JsonFormatter(),
                Path.Combine(logDirectory, "log-.json"),
                rollingInterval: RollingInterval.Day,
                retainedFileTimeLimit: retention)
            .CreateLogger();
    }
}
