using ArturRios.Output;
using ArturRios.Util.Http;
using ArturRios.Util.WebApi.Security.Extensions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace ArturRios.Heimdall.WebApi.Security;

/// <summary>
///     Global authorization filter enforcing FR-2F-10/NFR-17: a request authenticated with a UC-38
///     challenge token — <see cref="IdentityUser.MfaPending" /> set — is rejected with
///     <c>401 Unauthorized</c>, with no exceptions.
/// </summary>
/// <remarks>
///     <para>
///         Registered globally (<c>Startup.ConfigureWebApi</c>), so it runs on every controller
///         action, including <c>POST /api/auth/2fa/verify</c> itself — that endpoint never actually
///         trips it, though, because it is <c>[AllowAnonymous]</c> and reads the challenge token as a
///         request body field (<c>VerifyTwoFactorAuthCommand.ChallengeToken</c>), never as a bearer
///         credential, so <c>AuthenticationMiddleware</c> never attaches an <see cref="IdentityUser" />
///         with <c>MfaPending</c> set for that call in the first place. This filter only ever fires
///         when a challenge token is misused as an <c>Authorization: Bearer</c> header against some
///         other endpoint — exactly the case FR-2F-10 wants blocked.
///     </para>
///     <para>
///         A request that carries no bearer token at all — every anonymous endpoint, and every
///         unauthenticated call to a protected one — resolves <see cref="HttpContextExtensions.GetUser{TUser}" />
///         to <c>null</c> here, which does not match the <c>MfaPending: true</c> pattern below, so
///         the filter is a no-op for it. It neither blocks nor requires authentication itself; it
///         only ever narrows an identity the pipeline already attached.
///     </para>
/// </remarks>
public class MfaPendingGuardFilter : IAuthorizationFilter
{
    public void OnAuthorization(AuthorizationFilterContext context)
    {
        var user = context.HttpContext.GetUser<IdentityUser>();

        if (user is not { MfaPending: true })
        {
            return;
        }

        var output = ProcessOutput.New.WithError(
            "A two-factor challenge is still pending completion; this token is not valid here.");

        context.Result = new ObjectResult(output) { StatusCode = HttpStatusCodes.Unauthorized };
    }
}
