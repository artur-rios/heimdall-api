using ArturRios.Heimdall.Command.Services;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Output;
using ArturRios.Util.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace ArturRios.Heimdall.WebApi.Security;

/// <summary>
///     Turns a <see cref="PasswordHashGateSaturatedException" /> into <c>503 Service Unavailable</c>
///     carrying <see cref="AuthMessages.AuthenticationTemporarilyUnavailable" /> (Threat Model
///     TH-03).
/// </summary>
/// <remarks>
///     <para>
///         A global exception filter rather than a catch in each handler, because saturation is not a
///         property of any one use case: every endpoint that derives a password can meet it — login,
///         password reset, person creation, enabling and disabling two-factor authentication — and
///         each already returns its own errors through its own message map. Adding the same entry to
///         five maps and the same try/catch to eight handlers would state one rule in thirteen
///         places.
///     </para>
///     <para>
///         503 rather than 500, and with <c>Retry-After</c>, because nothing is wrong: the request
///         was well formed, the caller is not at fault, and the condition clears on its own. That is
///         also why it must not be logged as an error — it is the gate working.
///     </para>
///     <para>
///         The response says nothing about the account, so an endpoint that answers this reveals no
///         more than a slow one would. It cannot be used to tell an existing address from an absent
///         one: the decoy verification AF-11a spends passes through the same gate as a real check,
///         so both meet saturation on the same terms.
///     </para>
/// </remarks>
public class PasswordHashSaturationFilter : IExceptionFilter
{
    /// <summary>
    ///     Seconds to advise the caller to wait. Shorter than the gate's own wait, since by the time
    ///     this is written the request has already spent that long queuing.
    /// </summary>
    private const int RetryAfterSeconds = 5;

    public void OnException(ExceptionContext context)
    {
        if (context.Exception is not PasswordHashGateSaturatedException)
        {
            return;
        }

        var output = ProcessOutput.New.WithError(AuthMessages.AuthenticationTemporarilyUnavailable);

        context.Result = new ObjectResult(output) { StatusCode = HttpStatusCodes.ServiceUnavailable };
        context.HttpContext.Response.Headers.RetryAfter = RetryAfterSeconds.ToString();

        // Marked handled so it is not also logged as an unhandled fault. A saturated gate under load
        // would otherwise fill the log with stack traces describing the protection doing its job.
        context.ExceptionHandled = true;
    }
}
