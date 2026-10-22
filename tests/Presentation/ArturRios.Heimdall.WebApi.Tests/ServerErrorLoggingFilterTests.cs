using System.Reflection;
using ArturRios.Heimdall.WebApi.Controllers;
using ArturRios.Heimdall.WebApi.Security;
using ArturRios.Util.Test.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Extensions.Logging;

namespace ArturRios.Heimdall.WebApi.Tests;

/// <summary>
///     Every 5xx an action produces leaves one server-side line with the route template, the status and
///     the trace id, and nothing the caller sent (#227).
/// </summary>
public class ServerErrorLoggingFilterTests
{
    private const string Template = "api/person/{publicId}";

    [UnitFact]
    public void GivenAnActionAnswering500_WhenTheResultIsExecuted_ThenOneErrorLineNamesTheRouteStatusAndTrace()
    {
        // Given
        var logger = new RecordingLogger();

        // When
        Run(logger, StatusCodes.Status500InternalServerError, out var traceId);

        // Then
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Contains(Template, entry.Message);
        Assert.Contains("500", entry.Message);
        Assert.Contains(traceId, entry.Message);
    }

    [UnitFact]
    public void GivenARequestWithAnIdQueryAndBody_WhenItAnswers500_ThenNoneOfThemIsLogged()
    {
        // Given
        var logger = new RecordingLogger();

        // When
        Run(logger, StatusCodes.Status500InternalServerError, out _);

        // Then the raw path, the query string and the caller's address never reach the line
        var entry = Assert.Single(logger.Entries);
        Assert.DoesNotContain("7f3c", entry.Message);
        Assert.DoesNotContain("someone@example.com", entry.Message);
        Assert.DoesNotContain("203.0.113.9", entry.Message);
    }

    [UnitFact]
    public void GivenAnActionAnswering503_WhenTheResultIsExecuted_ThenItIsLoggedAtWarning()
    {
        // Given
        var logger = new RecordingLogger();

        // When
        Run(logger, StatusCodes.Status503ServiceUnavailable, out _);

        // Then a saturated gate or a readiness probe is the system working, not a fault
        Assert.Equal(LogLevel.Warning, Assert.Single(logger.Entries).Level);
    }

    [UnitFact]
    public void GivenTwoReadinessProbesAnswering503_WhenTheirResultsAreExecuted_ThenNoRequestLineIsLogged()
    {
        // Given the real readiness action's own metadata, as routing would hand it to the filter
        var logger = new RecordingLogger();
        var metadata = typeof(HealthCheckController).GetMethod(nameof(HealthCheckController.Ready))!
            .GetCustomAttributes(inherit: true);

        // When two probes within one cache window are answered 503
        Run(logger, StatusCodes.Status503ServiceUnavailable, out _, metadata);
        Run(logger, StatusCodes.Status503ServiceUnavailable, out _, metadata);

        // Then the cache's own once-per-window Warning is the only line, so the filter adds none
        Assert.Empty(logger.Entries);
    }

    [UnitTheory]
    [InlineData(StatusCodes.Status200OK)]
    [InlineData(StatusCodes.Status400BadRequest)]
    [InlineData(StatusCodes.Status401Unauthorized)]
    [InlineData(StatusCodes.Status404NotFound)]
    public void GivenAnActionAnsweringBelow500_WhenTheResultIsExecuted_ThenNothingIsLogged(int statusCode)
    {
        // Given
        var logger = new RecordingLogger();

        // When
        Run(logger, statusCode, out _);

        // Then
        Assert.Empty(logger.Entries);
    }

    private static void Run(RecordingLogger logger, int statusCode, out string traceId,
        IEnumerable<object>? endpointMetadata = null)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Response.StatusCode = statusCode;
        httpContext.Request.Path = "/api/person/7f3c0000-0000-0000-0000-000000000000";
        httpContext.Request.QueryString = new QueryString("?email=someone@example.com");
        httpContext.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("203.0.113.9");
        httpContext.TraceIdentifier = "trace-for-the-test";
        traceId = httpContext.TraceIdentifier;

        var descriptor = new ActionDescriptor
        {
            AttributeRouteInfo = new AttributeRouteInfo { Template = Template },
            EndpointMetadata = [.. endpointMetadata ?? []]
        };
        var actionContext = new ActionContext(httpContext, new RouteData(), descriptor);
        var context = new ResultExecutedContext(actionContext, [], new StatusCodeResult(statusCode), new object());

        new ServerErrorLoggingFilter(logger).OnResultExecuted(context);
    }

    private sealed class RecordingLogger : ILogger<ServerErrorLoggingFilter>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add((logLevel, formatter(state, exception)));
    }
}
