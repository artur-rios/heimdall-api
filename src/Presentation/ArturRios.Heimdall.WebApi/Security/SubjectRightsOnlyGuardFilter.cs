using ArturRios.Output;
using ArturRios.Util.Http;
using ArturRios.Util.WebApi.Security.Extensions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace ArturRios.Heimdall.WebApi.Security;

/// <summary>
///     Global authorization filter confining a subject-rights-only token — <see cref="IdentityUser.SubjectRightsOnly" />
///     set, issued at sign-in to an identity whose processing is restricted (NFR-24) — to the actions
///     marked <see cref="DataSubjectRightAttribute" />, refusing it everywhere else with
///     <c>403 Forbidden</c>.
/// </summary>
/// <remarks>
///     <para>
///         <see cref="ActorLivenessFilter" /> already refuses a restricted identity's token outside
///         those actions, but only while the restriction stands. This filter keeps the token what it
///         was issued as once the restriction is lifted: a subject's means to exercise their own
///         rights, not a way back into the account. A subject who lifts their restriction signs in
///         again for a full token.
///     </para>
///     <para>
///         <c>403</c> rather than the <c>401</c> <see cref="MfaPendingGuardFilter" /> answers with:
///         the token is genuine and current, and signing in again would only issue another like it
///         while the restriction stands. The caller is authenticated and not permitted here.
///     </para>
///     <para>
///         A request carrying no bearer token resolves to <c>null</c> and is left alone, as the
///         other global filters leave it.
///     </para>
/// </remarks>
public class SubjectRightsOnlyGuardFilter : IAuthorizationFilter
{
    /// <summary>The refusal, naming what the token is for so a client can route the subject there.</summary>
    public const string SubjectRightsOnly =
        "Processing of your identity is restricted. This token only admits exporting your data, requesting its erasure, and restricting or lifting the restriction on its processing.";

    public void OnAuthorization(AuthorizationFilterContext context)
    {
        var user = context.HttpContext.GetUser<IdentityUser>();

        if (user is not { SubjectRightsOnly: true } ||
            context.ActionDescriptor.EndpointMetadata.OfType<DataSubjectRightAttribute>().Any())
        {
            return;
        }

        var output = ProcessOutput.New.WithError(SubjectRightsOnly);

        context.Result = new ObjectResult(output) { StatusCode = HttpStatusCodes.Forbidden };
    }
}
