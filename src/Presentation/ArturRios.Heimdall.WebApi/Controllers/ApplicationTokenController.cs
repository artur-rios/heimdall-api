using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Mediator.Command;
using ArturRios.Output;
using ArturRios.Util.WebApi.AspNetCore;
using ArturRios.Util.WebApi.Security.Attributes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace ArturRios.Heimdall.WebApi.Controllers;

/// <summary>
///     Authentication for applications (UC-56, UC-57, FR-AP-13…FR-AP-16): exchanging a credential
///     for an application token, and checking such a token live.
/// </summary>
/// <remarks>
///     Both actions are anonymous to <c>AuthenticationMiddleware</c>. The middleware knows only
///     person tokens — and refuses application tokens (TH-35) — so UC-56 is authenticated by its
///     secret and UC-57 validates the bearer token itself.
/// </remarks>
[Route("api/applications")]
public class ApplicationTokenController(CommandMediator commandMediator) : Controller
{
    /// <summary>
    ///     Exchanges an application's secret for a token valid for ten minutes by default (UC-56).
    ///     Every refusal is the same 401, so the endpoint reveals nothing about which applications,
    ///     scopes or credentials exist. Rate-limited per client address like the person sign-in.
    /// </summary>
    [HttpPost("token")]
    [AllowAnonymous]
    [EnableRateLimiting(Startup.AuthEndpointRateLimitPolicy)]
    public async Task<ActionResult<DataOutput<IssueApplicationTokenCommandOutput?>>> IssueToken(
        [FromBody] IssueApplicationTokenCommand command)
    {
        // AF-56a — a body that is not JSON, or carries an id that is not a GUID, binds no command
        // (the controller is not [ApiController], so nothing answers for it). Say only that.
        if (command is null || !ModelState.IsValid)
        {
            return DataOutput<IssueApplicationTokenCommandOutput?>.New
                .WithError(ApplicationTokenMessages.RequestMalformed)
                .ToActionResult(statusMap: ApplicationTokenMessageMap.StatusCodes);
        }

        var result = await commandMediator
            .ExecuteCommandAsync<IssueApplicationTokenCommand, IssueApplicationTokenCommandOutput>(command);

        return result.ToActionResult(statusMap: ApplicationTokenMessageMap.StatusCodes);
    }
}
