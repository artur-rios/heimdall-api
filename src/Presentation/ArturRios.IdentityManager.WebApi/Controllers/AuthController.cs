using ArturRios.IdentityManager.Command.Input;
using ArturRios.IdentityManager.Command.Output;
using ArturRios.IdentityManager.Shared.Messages;
using ArturRios.Mediator.Command;
using ArturRios.Output;
using ArturRios.Util.WebApi.AspNetCore;
using ArturRios.Util.WebApi.Security.Attributes;
using Microsoft.AspNetCore.Mvc;

namespace ArturRios.IdentityManager.WebApi.Controllers;

[Route("api/auth")]
public class AuthController(CommandMediator commandMediator) : Controller
{
    /// <summary>
    ///     Authenticates a person by email and password and returns a token (UC-11, FR-AU-01…07). A
    ///     <c>User</c> also sends the <c>PublicId</c> of their scope; a <c>ScopeAdmin</c> or
    ///     <c>SystemAdmin</c> sends credentials only. Open to anonymous callers — this is where a
    ///     caller gets the token every other endpoint requires. Every rejection (AF-11a…AF-11e)
    ///     answers 401 alike, so the endpoint cannot be used to enumerate accounts.
    /// </summary>
    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<ActionResult<DataOutput<LoginCommandOutput?>>> Login(
        [FromBody] LoginCommand command)
    {
        var result = await commandMediator
            .ExecuteCommandAsync<LoginCommand, LoginCommandOutput>(command);

        return ResponseResolver.Resolve(result, statusMap: AuthMessageMap.StatusCodes);
    }
}
