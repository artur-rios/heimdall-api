using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Query.Input;
using ArturRios.Heimdall.Query.Output;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Mediator.Command;
using ArturRios.Mediator.Query;
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
public class ApplicationTokenController(CommandMediator commandMediator, QueryMediator queryMediator) : Controller
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
        // AF-56a — a body that is not JSON, empty, or carries an id that is not a GUID never reaches
        // here: MalformedRequestFilter answers it (NFR-10).
        var result = await commandMediator
            .ExecuteCommandAsync<IssueApplicationTokenCommand, IssueApplicationTokenCommandOutput>(command);

        return result.ToActionResult(statusMap: ApplicationTokenMessageMap.StatusCodes);
    }

    /// <summary>
    ///     Checks an application token live (UC-57), presented as <c>Authorization: Bearer …</c>.
    ///     Answers the application, its scope and the credential when the token is valid and all
    ///     three are still live; otherwise one 401. Anonymous to the middleware, which accepts only
    ///     person tokens: the handler validates the token itself.
    /// </summary>
    [HttpGet("self")]
    [AllowAnonymous]
    public async Task<ActionResult<DataOutput<ApplicationTokenCheckOutput?>>> Self()
    {
        var query = new CheckApplicationTokenQuery { Token = BearerToken(Request) };

        var result = await queryMediator
            .ExecuteQueryAsync<CheckApplicationTokenQuery, ApplicationTokenCheckOutput>(query);

        return result.ToActionResult(statusMap: ApplicationTokenMessageMap.StatusCodes);
    }

    /// <summary>
    ///     The token of a <c>Bearer</c> <c>Authorization</c> header, or <c>null</c>. The scheme is
    ///     matched case-insensitively (RFC 9110 §11.1); any other scheme, or none, is no token.
    /// </summary>
    private static string? BearerToken(HttpRequest request)
    {
        const string scheme = "Bearer ";
        var header = request.Headers.Authorization.ToString();

        return header.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)
            ? header[scheme.Length..].Trim()
            : null;
    }
}
