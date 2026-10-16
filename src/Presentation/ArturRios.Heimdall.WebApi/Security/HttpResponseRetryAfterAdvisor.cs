using System.Globalization;
using ArturRios.Heimdall.Command.Services;

namespace ArturRios.Heimdall.WebApi.Security;

/// <summary>
///     <see cref="IRetryAfterAdvisor" /> that sets the current response's <c>Retry-After</c> header,
///     in whole seconds and at least one — the form the rate limiter's own rejections use.
/// </summary>
public class HttpResponseRetryAfterAdvisor(IHttpContextAccessor httpContextAccessor) : IRetryAfterAdvisor
{
    public void Advise(TimeSpan wait)
    {
        if (httpContextAccessor.HttpContext is not { Response.HasStarted: false } httpContext)
        {
            return;
        }

        var seconds = Math.Max(1, (long)Math.Ceiling(wait.TotalSeconds));

        httpContext.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
    }
}
