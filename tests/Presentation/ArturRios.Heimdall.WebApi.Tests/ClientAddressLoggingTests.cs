using ArturRios.Configuration.Enums;
using ArturRios.Heimdall.WebApi.Tests.Support;
using ArturRios.Messaging.Email;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Functional;
using ArturRios.Util.WebApi.Middleware;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace ArturRios.Heimdall.WebApi.Tests;

/// <summary>
///     Every request is logged with its client IP address, by Util.WebApi's
///     <c>TraceActivityMiddleware</c> — security telemetry the Data Retention Schedule and the
///     Privacy Notice both declare.
/// </summary>
/// <remarks>
///     Which address that is — the caller's or the proxy's — is <c>TrustedProxyTests</c>' concern.
///     These pin that the line is written at all: that the middleware runs, and that nothing holds
///     its logger below the level it writes at.
/// </remarks>
[Collection(nameof(FunctionalCollection))]
public class ClientAddressLoggingTests() : WebApiTest<Program>(EnvironmentType.Local)
{
    [FunctionalFact]
    public async Task GivenAnyRequest_WhenItIsServed_ThenTraceActivityMiddlewareHasRunOnIt()
    {
        var response = await Gateway.Client.GetAsync("/HealthCheck");

        Assert.True(response.Headers.Contains("traceparent"));
    }

    [FunctionalFact]
    public void GivenTheRunningApi_WhenTraceActivityMiddlewareLogsARequest_ThenItIsWritten()
    {
        var logger = Log.ForContext(Constants.SourceContextPropertyName, typeof(TraceActivityMiddleware).FullName);

        Assert.True(logger.IsEnabled(LogEventLevel.Information));
    }

    [FunctionalFact]
    public void GivenTheRunningApi_WhenTheMailgunLibraryLogsASend_ThenNothingIsWritten()
    {
        // NFR-22: the library's own lines carry the recipient's address in the clear, at Information
        // on success and Error on a rejection. MailgunSender logs the same outcomes under a
        // LogSafeEmail reference, so the library's are silenced rather than duplicated.
        var logger = Log.ForContext(Constants.SourceContextPropertyName, typeof(MailgunEmailService).FullName);

        Assert.False(logger.IsEnabled(LogEventLevel.Information));
        Assert.False(logger.IsEnabled(LogEventLevel.Error));
    }
}
