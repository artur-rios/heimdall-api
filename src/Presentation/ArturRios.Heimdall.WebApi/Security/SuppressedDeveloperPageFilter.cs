using System.Runtime.ExceptionServices;
using ArturRios.Util.WebApi.Middleware;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArturRios.Heimdall.WebApi.Security;

/// <summary>
///     NFR-28: answers a failure that reaches a developer exception page the decision has turned off
///     with the API's own 500 envelope instead of the page.
/// </summary>
/// <remarks>
///     <para>
///         In Development the minimal host adds the developer exception page by itself, inside the
///         startup filters and ahead of the standard pipeline, whatever <see cref="SensitiveDiagnostics" />
///         says. ExceptionMiddleware sits inside it, so most failures never reach it, but one raised
///         ahead of that middleware (in routing, say) would be shown with its stack trace. The page's
///         exception filters are the hook the framework offers: this one, registered only where the
///         decision keeps the page off, never hands the failure on to the page.
///     </para>
///     <para>
///         The failure is rethrown into a fresh ExceptionMiddleware, so the body is exactly what the
///         standard pipeline writes for any other unhandled exception. The page has already logged
///         the exception, so that middleware logs nothing more.
///     </para>
/// </remarks>
public sealed class SuppressedDeveloperPageFilter : IDeveloperPageExceptionFilter
{
    public Task HandleExceptionAsync(ErrorContext errorContext, Func<ErrorContext, Task> next)
    {
        var failure = ExceptionDispatchInfo.Capture(errorContext.Exception);

        var envelope = new ExceptionMiddleware(_ =>
        {
            failure.Throw();

            return Task.CompletedTask;
        }, NullLogger<ExceptionMiddleware>.Instance);

        return envelope.InvokeAsync(errorContext.HttpContext);
    }
}
