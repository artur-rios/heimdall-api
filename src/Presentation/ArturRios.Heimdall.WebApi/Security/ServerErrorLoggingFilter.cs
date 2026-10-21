using System.Diagnostics;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace ArturRios.Heimdall.WebApi.Security;

/// <summary>
///     Writes one log line for every 5xx response an action produces — at Error, with the route
///     template, the status code and the trace id (#227).
/// </summary>
/// <remarks>
///     <para>
///         Handlers return persistence and other server faults to the client as a message and a status
///         code, and nothing else wrote them down: the response said "failed" and the log said nothing,
///         so the cause of a 500 could not be found from the trace id the caller held. A result filter
///         sees every such response whatever produced it, so no handler has to remember to log.
///     </para>
///     <para>
///         An exception that escapes the action never reaches a result filter: <c>ExceptionMiddleware</c>
///         catches it and already logs it at Error with its stack trace, inside the request's activity,
///         so this filter does not log it a second time. The two together leave one line per 5xx.
///     </para>
///     <para>
///         The route is the template (<c>api/person/{publicId}</c>), never the raw path, and the line
///         carries no query string, body, header or caller identity: ids and addresses in a log are
///         personal data the retention schedule would then have to cover (NFR-22). 503 is logged at
///         Warning instead — in this API it is always a deliberate answer (a saturated password-hash
///         gate, which <see cref="PasswordHashSaturationFilter" /> keeps out of the error log, or a
///         readiness probe reporting the database down, whose cause <c>DatabaseHealthCheck</c> already
///         logs), and a probe every few seconds would otherwise bury real faults.
///     </para>
/// </remarks>
public class ServerErrorLoggingFilter(ILogger<ServerErrorLoggingFilter> logger) : IAlwaysRunResultFilter
{
    public void OnResultExecuting(ResultExecutingContext context)
    {
    }

    public void OnResultExecuted(ResultExecutedContext context)
    {
        var statusCode = context.HttpContext.Response.StatusCode;

        if (statusCode < StatusCodes.Status500InternalServerError)
        {
            return;
        }

        var route = context.ActionDescriptor.AttributeRouteInfo?.Template ?? context.ActionDescriptor.DisplayName;
        var traceId = Activity.Current?.TraceId.ToString() ?? context.HttpContext.TraceIdentifier;
        var level = statusCode == StatusCodes.Status503ServiceUnavailable ? LogLevel.Warning : LogLevel.Error;

        logger.Log(level, "Request to {Route} answered {StatusCode} (trace {TraceId})", route, statusCode, traceId);
    }
}
