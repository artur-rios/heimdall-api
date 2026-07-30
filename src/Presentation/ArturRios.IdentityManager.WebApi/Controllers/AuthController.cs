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

    /// <summary>
    ///     Requests a password reset link (UC-12, FR-PR-01/02). A <c>User</c> also sends the
    ///     <c>PublicId</c> of their scope; a <c>ScopeAdmin</c> or <c>SystemAdmin</c> sends the email
    ///     alone. Open to anonymous callers — someone who has lost their password cannot hold a
    ///     token.
    /// </summary>
    /// <remarks>
    ///     Answers 200 with the same message whether or not the address belongs to anyone (AF-12a),
    ///     so the endpoint cannot be used to enumerate accounts. The only rejection is a malformed
    ///     request (400, NFR-10), which says nothing about who is registered.
    /// </remarks>
    [HttpPost("password-recovery")]
    [AllowAnonymous]
    public async Task<ActionResult<DataOutput<PasswordRecoveryCommandOutput?>>> PasswordRecovery(
        [FromBody] PasswordRecoveryCommand command)
    {
        var result = await commandMediator
            .ExecuteCommandAsync<PasswordRecoveryCommand, PasswordRecoveryCommandOutput>(command);

        return ResponseResolver.Resolve(result, statusMap: AuthMessageMap.StatusCodes);
    }

    /// <summary>
    ///     Sets a new password from the reset token mailed by UC-12 (UC-13, FR-PR-03/04). Open to
    ///     anonymous callers for the same reason: the token is the only credential someone who has
    ///     lost their password can present.
    /// </summary>
    /// <remarks>
    ///     Unlike the two endpoints above, each rejection is named — unknown (AF-13c), expired
    ///     (AF-13a), and spent (AF-13b) tokens all answer 400 with their own message, as does a
    ///     malformed request (AF-13d). Nothing is disclosed by the distinction: the token identifies
    ///     no account to a caller who does not already hold it.
    /// </remarks>
    [HttpPost("password-reset")]
    [AllowAnonymous]
    public async Task<ActionResult<DataOutput<ResetPasswordCommandOutput?>>> ResetPassword(
        [FromBody] ResetPasswordCommand command)
    {
        var result = await commandMediator
            .ExecuteCommandAsync<ResetPasswordCommand, ResetPasswordCommandOutput>(command);

        return ResponseResolver.Resolve(result, statusMap: AuthMessageMap.StatusCodes);
    }
}
