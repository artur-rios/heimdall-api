using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Output;
using ArturRios.Util.WebApi.AspNetCore;
using Microsoft.AspNetCore.Mvc.Filters;

namespace ArturRios.Heimdall.WebApi.Binding;

/// <summary>
///     Answers <c>400 Bad Request</c> with <see cref="RequestMessages.Malformed" /> when the request
///     could not be bound to the action's parameters — a body that is not JSON, an empty one the
///     action requires, or a value of the wrong type in the body or query string (NFR-10,
///     issue #186).
/// </summary>
/// <remarks>
///     <para>
///         The controllers are not <c>[ApiController]</c>, so nothing refuses a failed binding: the
///         action ran anyway with a null or half-filled argument, which surfaced as a 500 (a body that
///         is not JSON), or as a request executed with a value the caller never sent (a query filter
///         of the wrong type silently dropped, answering 200 unfiltered). Adding the attribute would
///         change every action's other conventions at once; this filter states only the one rule.
///     </para>
///     <para>
///         An action filter, because that is the stage that runs after model binding (which fills
///         <c>ModelState</c>) and after every authorization filter. An authorization filter would run
///         before binding and see nothing; and running after them keeps an unauthenticated or
///         forbidden caller answering 401 or 403 rather than learning anything about the shape of an
///         endpoint's input. Global, so no endpoint can be added that forgets it.
///     </para>
///     <para>
///         The response carries no binding detail: the framework's own messages name properties and
///         types of the internal model, which the caller has no use for.
///     </para>
/// </remarks>
public class MalformedRequestFilter : IActionFilter
{
    public void OnActionExecuting(ActionExecutingContext context)
    {
        if (context.ModelState.IsValid)
        {
            return;
        }

        context.Result = DataOutput<object?>.New
            .WithError(RequestMessages.Malformed)
            .ToActionResult(statusMap: RequestMessageMap.StatusCodes)
            .Result;
    }

    public void OnActionExecuted(ActionExecutedContext context)
    {
    }
}
