using ArturRios.IdentityManager.Command.Input;
using ArturRios.IdentityManager.Command.Output;
using ArturRios.IdentityManager.Shared.Messages;
using ArturRios.Mediator.Command;
using ArturRios.Output;
using ArturRios.IdentityManager.WebApi.Security;
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

    /// <summary>
    ///     Confirms an email address from the verification token mailed at person creation (UC-14,
    ///     FR-EV-03). Open to anonymous callers: the person reaches this from a link in their mail
    ///     client, where they hold no bearer token — and the point of the link is that they have not
    ///     proved anything yet.
    /// </summary>
    /// <remarks>
    ///     Each rejection is named, as UC-13's are — unknown (AF-14c), expired (AF-14a), and spent
    ///     (AF-14b) tokens all answer 400 with their own message, as does a request carrying no token
    ///     at all. An address that was already verified answers 200: UC-14 defines no alternative flow
    ///     for it, and the link did what it promised.
    /// </remarks>
    [HttpPost("verify-email")]
    [AllowAnonymous]
    public async Task<ActionResult<DataOutput<VerifyEmailCommandOutput?>>> VerifyEmail(
        [FromBody] VerifyEmailCommand command)
    {
        var result = await commandMediator
            .ExecuteCommandAsync<VerifyEmailCommand, VerifyEmailCommandOutput>(command);

        return ResponseResolver.Resolve(result, statusMap: AuthMessageMap.StatusCodes);
    }

    /// <summary>
    ///     Retires the caller's outstanding verification links and mails a fresh one (UC-15,
    ///     FR-EV-04), for someone whose first link expired, was lost, or never arrived. The one
    ///     authenticated endpoint on this controller — and the reason it takes no request body: the
    ///     person is read from the bearer token, so a caller can only ever ask for their own link.
    /// </summary>
    /// <remarks>
    ///     No <c>RoleRequirement</c>: the authorization matrix grants email verification to all three
    ///     roles and withholds it from anonymous callers, which is exactly what authentication alone
    ///     enforces. An address that is already verified answers 400 (AF-15a) — unlike UC-14's
    ///     idempotent success, since a link mailed to a verified address could do nothing when clicked.
    ///     A token naming a person who no longer exists answers 404: it was validated, but there is no
    ///     address left to send to.
    /// </remarks>
    [HttpPost("resend-verification")]
    public async Task<ActionResult<DataOutput<ResendVerificationEmailCommandOutput?>>> ResendVerification()
    {
        var command = new ResendVerificationEmailCommand();
        HttpContext.ApplyActor(command);

        var result = await commandMediator
            .ExecuteCommandAsync<ResendVerificationEmailCommand, ResendVerificationEmailCommandOutput>(
                command);

        return ResponseResolver.Resolve(result, statusMap: AuthMessageMap.StatusCodes);
    }

    /// <summary>
    ///     Signs a Google account up or in against a scope and returns a token (UC-25,
    ///     FR-GO-03…FR-GO-13). The caller sends the ID token they obtained from Google and the
    ///     <c>PublicId</c> of the scope they are entering; the first call for a given Google account
    ///     in a given scope creates the Google User, every later one authenticates it. Open to
    ///     anonymous callers for the same reason <c>login</c> is — this is where a Google User gets
    ///     the token every other endpoint requires.
    /// </summary>
    /// <remarks>
    ///     Both 401 flows answer alike (AF-25a, AF-25d), as UC-11's do, so an anonymous caller cannot
    ///     use the endpoint to learn which Google accounts a scope has registered or which were
    ///     deleted. AF-25b answers 403 for a missing, deleted, and disabled scope alike — a scope is
    ///     not enumerable through here either. Only AF-25c (409) is named, and it discloses nothing:
    ///     the caller has already proved the address is theirs.
    /// </remarks>
    [HttpPost("google")]
    [AllowAnonymous]
    public async Task<ActionResult<DataOutput<GoogleSignInCommandOutput?>>> GoogleSignIn(
        [FromBody] GoogleSignInCommand command)
    {
        var result = await commandMediator
            .ExecuteCommandAsync<GoogleSignInCommand, GoogleSignInCommandOutput>(command);

        return ResponseResolver.Resolve(result, statusMap: AuthMessageMap.StatusCodes);
    }

    /// <summary>
    ///     Ends the caller's Google-authenticated session (UC-26, FR-GO-18). Takes no request body for
    ///     the reason <c>resend-verification</c> does not: the Google User is read from the bearer
    ///     token, so a caller can only ever sign themselves out.
    /// </summary>
    /// <remarks>
    ///     No <c>RoleRequirement</c>, deliberately. The authorization matrix grants Google sign-out to
    ///     a Google User acting on themselves and marks it not-applicable for both administrator roles
    ///     — who can never be Google Users (FR-GO-04) — so the rule is "the caller is a live Google
    ///     User", which is data the attribute cannot see and the handler checks. It also keeps the
    ///     endpoint to the two answers UC-26 defines: 200, or 401 for every rejection. A missing or
    ///     malformed token is the other half of AF-26a and never reaches here — authentication answers
    ///     it with the same 401.
    /// </remarks>
    [HttpPost("google/sign-out")]
    public async Task<ActionResult<DataOutput<GoogleSignOutCommandOutput?>>> GoogleSignOut()
    {
        var command = new GoogleSignOutCommand();
        HttpContext.ApplyActor(command);

        var result = await commandMediator
            .ExecuteCommandAsync<GoogleSignOutCommand, GoogleSignOutCommandOutput>(command);

        return ResponseResolver.Resolve(result, statusMap: AuthMessageMap.StatusCodes);
    }
}
